#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using RulesMSBuild.Tools.Builder.Caching;
using RulesMSBuild.Tools.Builder.Diagnostics;
using RulesMSBuild.Tools.Builder.Launcher;
using RulesMSBuild.Tools.Builder.MSBuild;
using static RulesMSBuild.Tools.Builder.BazelLogger;

namespace RulesMSBuild.Tools.Builder
{
    public class BuilderDependencies
    {
        public List<ILogger> Loggers;
        public IBazelMsBuildLogger BuildLog;
        public PathMapper PathMapper;
        public BuildCache Cache;
        public ProjectLoader ProjectLoader;

        public BuilderDependencies(BuildContext context, IBazelMsBuildLogger? buildLog = null)
        {
            PathMapper = new PathMapper(context.Bazel.OutputBase, context.Bazel.ExecRoot);
            Cache = new BuildCache(context.Bazel.Label, PathMapper, new Files(), context.TargetGraph);
            ProjectLoader = new ProjectLoader(context.ProjectFile, Cache, PathMapper, context.TargetGraph);
            BuildLog = buildLog ?? new BazelMsBuildLogger(
                m =>
                {
                    Console.Out.Write(m);
                    Console.Out.Flush();
                },
                context.DiagnosticsEnabled ? LoggerVerbosity.Normal : LoggerVerbosity.Quiet,
                (m) => PathMapper.ToRelative(m));

            Loggers = new List<ILogger>() { BuildLog };
            if (context.DiagnosticsEnabled)
            {
                var path = context.OutputPath(context.Bazel.Label.Name + ".binlog");
                Debug($"added binlog {path}");
                var binlog = new BinaryLogger() { Parameters = path };
                Loggers.Add(binlog);
            }

            if (context.TargetGraph != null)
            {
                Loggers.Add(new TargetGraphLogger(context.TargetGraph!, PathMapper));
            }
        }
    }

    public class Builder
    {
        private readonly BuildContext _context;
        private readonly BuilderDependencies _deps;
        private readonly string _action;
        private readonly TargetGraph? _targetGraph;
        private BuildParameters _buildParameters;
        private readonly BuildManager _buildManager;
        private readonly IBazelMsBuildLogger _log;

        public Builder(BuildContext context, BuilderDependencies deps)
        {
            _context = context;
            _deps = deps;
            _action = _context.Command.Action.ToLower();
            _buildManager = BuildManager.DefaultBuildManager;
            _targetGraph = context.TargetGraph;
            _log = deps.BuildLog;
        }

        public int Build()
        {
            Debug("$exec_root: " + _context.Bazel.ExecRoot);
            Debug("$output_base: " + _context.Bazel.OutputBase);
            ProjectInstance? project = null;
            try
            {
                // Hygiene: remove any obj/ and bin/ that have leaked into the REAL source tree (typically
                // from a direct `dotnet build`/`dotnet test` outside Bazel). Bazel's own intermediates live
                // under bazel-out; these source-tree copies are stray clutter that can confuse tooling and,
                // because Windows has no Bazel sandbox, risk being picked up by an in-place action. Clean
                // before the action so the build starts from a tidy source tree.
                CleanSourceTreeArtifacts();

                // Hermeticity: Windows has no Bazel action sandbox, and Bazel does not clear a declared
                // output directory (TreeArtifact) before re-running an action. The previous build's
                // bazel-out obj/ therefore persists across runs, so MSBuild's CoreCompile up-to-date check
                // can find a stale IntermediateAssembly and skip recompilation -- emitting a stale DLL that
                // Bazel then caches. Delete obj/ before a build so a source change always forces a correct
                // recompile. Only obj/ is removed; the restore outputs (project.assets.json, *.nuget.g.props)
                // live under a sibling restore/ directory and are inputs to this action, so they are intact.
                if (_action == "build")
                    CleanIntermediateOutput();

                project = BeginBuild();
                if (project == null) return -1;

                var result = ExecuteBuild(project);

                // Coverity C# capture: after a successful build, emit a self-contained,
                // restore-suppressed `dotnet build` response file so bb's live cov-build pass can
                // replay the compile outside Bazel, where MSBuild spawns a discrete csc child that
                // Coverity's process monitor recognizes.
                if (_action == "build"
                    && result == BuildResultCode.Success
                    && !string.IsNullOrEmpty(_context.Command.CoverityRsp))
                {
                    WriteCoverityRsp(_context.Command.CoverityRsp!);
                }

                EndBuild(result);
                return (int)result;
            }
            catch (Exception ex)
            {
                var shouldThrow = true;
                if (ex is BazelException)
                {
                    _log.Error(ex.Message);
                    shouldThrow = false;
                }

                try
                {
                    _buildManager.EndBuild();
                    foreach (var logger in _deps.Loggers)
                    {
                        try
                        {
                            logger.Shutdown();
                        }
                        catch
                        {
                            // ignored
                        }
                    }
                }
                catch
                {
                    //ignored
                }

                if (shouldThrow)
                    throw;
                return 1;
            }
            finally
            {
                _buildManager.Dispose();
                // Clean again after the action so we never leave obj/bin behind in the source tree,
                // regardless of success or failure. The real outputs live under bazel-out and are untouched.
                CleanSourceTreeArtifacts();
            }
        }

        /// <summary>
        /// Serializes a self-contained, restore-suppressed <c>dotnet build</c> response file for this
        /// project so bb's live Coverity C# pass can replay <c>cov-build -- dotnet build @&lt;rsp&gt;</c>
        /// outside the Bazel sandbox. The replay must run with cwd == exec-root (so Directory.Bazel.props
        /// derives the right BazelPackage) and with the SDK env (DOTNET_ROOT etc.) set by bb. MSBuild then
        /// spawns a discrete csc child -- which Coverity's <c>--bazel</c> replay path cannot see, because
        /// the normal build compiles in-process via builder.dll (see servicemesh plan / memory
        /// coverity-csharp-live-pass-spike-proven).
        /// </summary>
        private void WriteCoverityRsp(string rspPath)
        {
            var msb = _context.MSBuild;

            // The declared Bazel output path is relative (bazel-out/.../X.coverity.rsp). By the time this
            // runs, BeginBuild() has already moved the CWD to the project directory (an exec-root junction
            // into the source tree), so a relative write would land under the source tree, not the exec
            // root where Bazel expects the output. Anchor it to ExecRoot, which is captured at
            // BuildContext construction (before any CWD change) and is the correct exec root.
            if (!Path.IsPathRooted(rspPath))
                rspPath = Path.Combine(_context.Bazel.ExecRoot, rspPath);

            // Emit paths with forward slashes: a response file treats a trailing backslash as a line
            // continuation, and dotnet/MSBuild accept '/' on Windows. The project path is the exec-root
            // path (_context.ProjectFile) so the replay, run from the exec-root, resolves BazelPackage.
            string Norm(string p) => p.Replace('\\', '/');

            var lines = new List<string>
            {
                Norm(_context.ProjectFile),
                "--no-restore",
                "-t:Build",
                "-p:Restore=false",
                "--no-incremental",
                "-nologo",
            };

            // Global properties override the csproj. Skip RestoreUseStaticGraphEvaluation: under the
            // SDK 10.x band it re-triggers a static-graph restore that rewrites the read-only
            // nuget.g.props even with Restore=false, failing the offline replay.
            foreach (var (name, value) in msb.GlobalProperties)
            {
                if (name == "RestoreUseStaticGraphEvaluation")
                    continue;
                lines.Add($"-p:{name}={Norm(value)}");
            }

            // BuildEnvironment values are normally exported as environment variables; re-express them as
            // explicit -p: so the response file is self-contained and does not depend on bb rebuilding
            // the builder's env. PublishDir points into read-only bazel-out and is unused by -t:Build, so
            // it is skipped. The replay-time writable-scratch paths (MSBuildProjectExtensionsPath,
            // IntermediateOutputPath, OutputPath) are deliberately NOT baked here -- they are volatile
            // per run, so bb appends them on the replay command line (a later -p: wins).
            //
            // UseAppHost is skipped too, and the reason is subtle: as an environment variable it is
            // MSBuild's LOWEST-precedence property, so an exe csproj's own <UseAppHost>true</UseAppHost>
            // overrides the builder's UseAppHost=false in the live build. Re-emitting it as -p: would
            // promote it to a GLOBAL property (highest precedence) that the csproj can no longer override,
            // forcing UseAppHost=false onto projects that set SelfContained=true -- an illegal combo that
            // fails the standalone replay with NETSDK1067. Omitting it lets the csproj value win in the
            // replay exactly as it does in the live build.
            //
            // NoWarn is skipped for the same precedence reason. The builder sets NoWarn=NU1603 (a restore-
            // phase NuGet warning) as an environment variable, so a csproj that appends its own suppressions
            // -- <NoWarn>$(NoWarn);1591</NoWarn> -- still wins in the live build. Re-emitting NoWarn=NU1603
            // as a global -p: has highest precedence, silently dropping the csproj's <NoWarn> assignment; a
            // project with TreatWarningsAsErrors=true and GenerateDocumentationFile=true then fails the
            // replay with CS1591 (missing XML doc). Omitting it is safe: NU1603 is a restore warning, and
            // the rsp already carries --no-restore + -p:Restore=false, so it cannot fire at replay.
            foreach (var (name, value) in msb.BuildEnvironment)
            {
                if (name == "PublishDir" || name == "UseAppHost" || name == "NoWarn")
                    continue;
                lines.Add($"-p:{name}={Norm(value)}");
            }

            var rspDir = Path.GetDirectoryName(rspPath);
            if (!string.IsNullOrEmpty(rspDir))
                Directory.CreateDirectory(rspDir);
            File.WriteAllLines(rspPath, lines);
            Debug($"wrote coverity rsp: {rspPath}");
        }

        /// <summary>
        /// Deletes obj/ and bin/ from the project's REAL source directory. These are stray leaks (typically
        /// from a direct `dotnet` invocation) -- the Bazel-declared intermediate/output TreeArtifacts live under
        /// bazel-out (<see cref="BazelContext.OutputDir"/>), never in the source tree, so this never removes a
        /// declared output. Guarded to no-op if the project directory is under bazel-out (generated projects).
        /// </summary>
        private void CleanSourceTreeArtifacts()
        {
            try
            {
                var projectDir = _context.ProjectDirectory;
                if (string.IsNullOrEmpty(projectDir) || !Directory.Exists(projectDir))
                    return;

                // Never touch anything under bazel-out: those obj/bin ARE declared Bazel outputs.
                var binDir = _context.Bazel.BinDir;
                if (!string.IsNullOrEmpty(binDir) &&
                    projectDir.StartsWith(binDir, StringComparison.OrdinalIgnoreCase))
                    return;

                foreach (var name in new[] { "obj", "bin" })
                {
                    var dir = Path.Combine(projectDir, name);
                    if (!Directory.Exists(dir))
                        continue;
                    try
                    {
                        // Bazel marks outputs read-only; clear the attribute before deleting.
                        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                            File.SetAttributes(f, FileAttributes.Normal);
                        Directory.Delete(dir, true);
                        Debug($"removed source-tree {name}/ at {dir}");
                    }
                    catch (Exception ex)
                    {
                        // Best-effort: never break an otherwise-good build over cleanup.
                        Debug($"could not remove source-tree {name}/ at {dir}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug($"CleanSourceTreeArtifacts failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes the intermediate obj/ directory (<see cref="MSBuildContext.IntermediateOutputPath"/>,
        /// i.e. bazel-out/.../bin/&lt;package&gt;/obj) before a build action. Because Windows has no Bazel sandbox
        /// and Bazel does not clear a declared TreeArtifact between runs, a stale obj/ left by a previous action
        /// makes MSBuild's incremental up-to-date check skip CoreCompile, producing a stale assembly that then
        /// gets cached. Deleting obj/ forces a clean compile. The restore outputs live under a separate sibling
        /// restore/ directory (BaseIntermediateOutputPath) and are inputs to this action, so they are never
        /// touched here. Best-effort: a deletion failure logs and lets the build proceed (pre-fix behavior).
        /// </summary>
        private void CleanIntermediateOutput()
        {
            var objDir = _context.MSBuild.IntermediateOutputPath;
            try
            {
                if (string.IsNullOrEmpty(objDir) || !Directory.Exists(objDir))
                    return;

                // Bazel marks prior outputs read-only; clear the attribute before deleting.
                foreach (var f in Directory.EnumerateFiles(objDir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(objDir, true);
                Debug($"removed stale intermediate obj/ at {objDir}");
            }
            catch (Exception ex)
            {
                Debug($"could not remove intermediate obj/ at {objDir}: {ex.Message}");
            }
        }

        public ProjectInstance? BeginBuild()
        {
            // GlobalProjectCollection loads EnvironmentVariables on Init. We use ExecRoot in the project files, we
            // can't use MSBuildStartupDirectory because NuGet Restore uses a static graph restore which starts up a
            // new process in the directory of the project file. We could set ExecRoot in the ProjectCollection Global
            // properties, but then we'd have to manage its value in the ConfigCache of the build manager later on.
            // Setting it here allows the project file to read it for paths and we don't have to clear it later.
            _context.SetEnvironment();

            if (!_deps.Cache.Initialize(_context.LabelPath(".cache_manifest"), _buildManager))
            {
                return null;
            }

            var pc = new ProjectCollection(
                _context.MSBuild.GlobalProperties,
                _deps.Loggers,
                ToolsetDefinitionLocations.Default);


            // our restore outputs are relative to the project directory
            Environment.CurrentDirectory = _context.ProjectDirectory;

            ProjectInstance project;
            try
            {
                project = _deps.ProjectLoader.Load(pc);
            }
            catch (ProjectCacheMissException missingException)
            {
                Fail($"invalid ProjectReference",
                    $"ProjectReference: \"{_deps.PathMapper.ToBazel(missingException.ProjectPath)}\" is not " +
                    $"listed in the deps attribute of {_context.Bazel.Label}. Did you remember to " +
                    $"`bazel run //:gazelle` after updating your project file?");
                return null;
            }

            if (!ValidateTfm(project))
                return null;

            // Advisory: warn once (on restore, the first action that reads the source .csproj) if the project
            // sets any property that rules_msbuild manages and will override, so the mismatch isn't silent.
            if (_action == "restore")
                WarnOverriddenProperties(pc);

            _buildParameters = new BuildParameters(pc)
            {
                EnableNodeReuse = false,
                DetailedSummary = true,
                Loggers = pc.Loggers,
                ResetCaches = false,
                MaxNodeCount = 1,
                LogTaskInputs = _context.DiagnosticsEnabled,
                ProjectLoadSettings = _context.DiagnosticsEnabled
                    ? ProjectLoadSettings.RecordEvaluatedItemElements
                    : ProjectLoadSettings.Default,
                // cult-copy
                ToolsetDefinitionLocations =
                    ToolsetDefinitionLocations.ConfigurationFile |
                    ToolsetDefinitionLocations.Registry,
                ProjectRootElementCache = pc.ProjectRootElementCache,
            };
            _buildManager.BeginBuild(_buildParameters);

            if (_deps.BuildLog.HasError)
            {
                Console.WriteLine("Failed to initialize build manager, please file an issue.");
                return null;
            }

            return project;
        }

        private BuildResultCode ExecuteBuild(ProjectInstance project)
        {
            var source = new TaskCompletionSource<BuildResultCode>();
            var flags = BuildRequestDataFlags.ReplaceExistingProjectInstance;

            var data = new BuildRequestData(
                project,
                _context.MSBuild.Targets, null, flags
            );

            switch (_action)
            {
                case "restore":
                    if (!Directory.Exists(_context.MSBuild.RestoreDir))
                    {
                        Directory.CreateDirectory(_context.MSBuild.RestoreDir);
                    }

                    _context.ProjectBazelProps["AssemblyName"] = _context.Command.assembly_name;
                    var writer = new BazelPropsWriter();
                    writer.WriteProperties(
                        _context.ProjectExtensionPath(".bazel.props"),
                        _context.ProjectBazelProps);
                    writer.WriteTargets(_context.ProjectExtensionPath(".bazel.targets"));
                    break;
                case "pack":
                    RegisterRunfiles(project);

                    // The default 'Pack' implementation by nuget sets a global property for the target framework
                    // this invalidates cache entries since they are keyed by ProjectFullPath + GlobalProperties
                    // We enforce a single target framework though, so this specification is not necessary
                    //
                    // additionally, it rebuilds targets that produce outputs, like writing to an Assembly References
                    // cache file, and Bazel will have those files marked as ReadOnly, so MSBuild will fail the build because
                    // it can't write to that file.

                    // to prevent rebuilding, we clone the configuration so MSBuild will reuse the results from previous
                    // builds.
                    _deps.Cache.CloneConfiguration(data, _buildParameters.DefaultToolsVersion, project);
                    break;
            }

            _buildManager.PendBuildRequest(data)
                .ExecuteAsync(submission =>
                {
                    var result = submission.BuildResult?.OverallResult ?? BuildResultCode.Failure;

                    if (submission.BuildResult?.Exception != null)
                    {
                        Error(submission.BuildResult.Exception.ToString());
                    }

                    source.SetResult(result);
                }, new object());

            if (_action == "publish")
            {
                // When the project builds its own native apphost (UseAppHost=true), MSBuild's publish
                // already emits <assembly_name>[.exe] as the executable. Synthesizing our launcher here
                // would land on that same path and clobber the apphost -- and on Linux it drags the Go
                // launcher's whole stdlib (and its CVEs) into every image for no runtime benefit. So
                // respect the project's choice: only provide our launcher when UseAppHost is off, which
                // is rules_msbuild's default (see MSBuildContext BuildEnvironment).
                var useAppHost = project.GetProperty("UseAppHost")?.EvaluatedValue;
                var projectOwnsLauncher = string.Equals(useAppHost, "true", StringComparison.OrdinalIgnoreCase);

                if (_context.IsExecutable && !projectOwnsLauncher)
                {
                    var launcherFactory = new LauncherFactory();
                    var launcherPath = Path.Combine(_context.MSBuild.PublishDir, _context.Command.assembly_name);
                    launcherFactory.CreatePublish(
                        Path.Combine(_context.Bazel.ExecRoot, _context.Command.LauncherTemplate),
                        launcherPath,
                        _context);
                }

                var runfilesDir = _context.Command.assembly_name + ".dll.runfiles";
                WriteRunfilesInfo(Path.Combine(_context.MSBuild.PublishDir, "runfiles.info"),
                    runfilesDir, true);
                CopyRunfiles(Path.Combine(_context.MSBuild.PublishDir, runfilesDir));
            }

            var resultCode = source.Task.GetAwaiter().GetResult();

            return resultCode;
        }

        private void RegisterRunfiles(ProjectInstance project)
        {
            var runfilesDir = Path.Combine(_context.MSBuild.PublishDir,
                _context.Command.assembly_name + ".dll.runfiles");
            var runfilesManifest = new FileInfo(Path.Combine(runfilesDir, "MANIFEST"));

            if (runfilesManifest.Exists)
            {
                foreach (var entry in File.ReadAllLines(runfilesManifest.FullName))
                {
                    var parts = entry.Split(' ');
                    var manifestPath = parts[0];

                    var filePath = Path.Combine(runfilesDir, parts[1]);
                    project.AddItem("None", filePath, new[]
                    {
                        new KeyValuePair<string, string>("Pack", "true"),
                        new KeyValuePair<string, string>("PackagePath", $"content/runfiles/{manifestPath}"),
                    });
                }
            }

            // the dll will be placed at    <root>/tools/<tfm>/any/<primaryName>.dll
            // runfiles will be at          <root>/content/runfiles
            var packDir = _context.OutputPath("pack");
            Directory.CreateDirectory(packDir);
            var path = Path.Combine(packDir, "runfiles.info");
            WriteRunfilesInfo(path, "../../../content/runfiles", true);
            project.AddItem("None", path, new[]
            {
                // new KeyValuePair<string, string>("What", "wow"),
                new KeyValuePair<string, string>("Pack", "true"),
                new KeyValuePair<string, string>("PackagePath", $"tools/{_context.Tfm}/any/"),
            });
        }

        private void CopyRunfiles(string runfilesDir)
        {
            var inputManifest = new FileInfo(_context.LabelPath(".runfiles_manifest"));

            if (!inputManifest.Exists) return;

            Directory.CreateDirectory(runfilesDir);
            var basePath = Path.GetDirectoryName(_context.Bazel.ExecRoot)!;
            var directories = new HashSet<string>();
            using var outputManifest = new StreamWriter(File.Create(Path.Combine(runfilesDir, "MANIFEST")));
            foreach (var line in File.ReadAllLines(inputManifest.FullName))
            {
                var ind = line.IndexOf(' ');
                string entry;
                string? fullPath = null;
                if (ind < 0)
                {
                    entry = line;
                }
                else
                {
                    entry = line[0..ind];
                    fullPath = Path.Combine(basePath, line[(ind + 1)..]);
                }

                outputManifest.Write(entry);

                var parts = entry.Split('/');
                if (parts.Length >= 1 && parts[1] == "external") continue;

                var directory = Path.Combine(runfilesDir, Path.GetDirectoryName(entry)!);
                if (directories.Add(directory))
                    Directory.CreateDirectory(directory);

                var destPath = Path.Combine(runfilesDir, entry);
                if (fullPath == null)
                    File.Create(destPath);
                else
                {
                    outputManifest.Write(" ");
                    outputManifest.WriteLine(entry);
                    var file = new FileInfo(fullPath);
                    if ((file.Attributes & FileAttributes.Directory) != 0)
                        CopyDirectory(fullPath, destPath);
                    else
                        File.Copy(fullPath, destPath, true);
                }
            }
        }

        private void CopyDirectory(string src, string dest)
        {
            Directory.CreateDirectory(dest);
            string Rel(string parent, string child) => child[(parent.Length + 1)..];
            foreach (var subDir in Directory.EnumerateDirectories(src))
            {
                var rel = Rel(src, subDir);
                CopyDirectory(subDir, Path.Combine(dest, rel));
            }

            foreach (var file in Directory.EnumerateFiles(src))
            {
                var rel = Rel(src, file);
                File.Copy(file, Path.Combine(dest, rel));
            }
        }

        /// <summary>
        /// Emits a warning for each property the source .csproj sets that rules_msbuild manages and will override.
        /// Two override mechanisms are covered: MSBuild global properties (set on the ProjectCollection, they always
        /// win over project-level &lt;PropertyGroup&gt; values) and the output-path properties forced by
        /// Directory.Bazel.props. Only property elements declared directly in the entry .csproj are inspected
        /// (imports are ignored). Best-effort: never fails the build.
        /// </summary>
        private void WarnOverriddenProperties(ProjectCollection pc)
        {
            // Output-path properties rules_msbuild pins via dotnet/private/msbuild/Directory.Bazel.props so that
            // outputs land in Bazel's tree. Keep in sync with that file.
            var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "OutputPath",
                "BaseIntermediateOutputPath",
                "IntermediateOutputPath",
                "PackageOutputPath",
                "NuspecOutputPath",
            };
            // Global properties always override project-level values; pull them straight from the context so this
            // stays correct as the set evolves.
            foreach (var key in _context.MSBuild.GlobalProperties.Keys)
                managed.Add(key);

            ProjectRootElement? root;
            try
            {
                // Open just the entry .csproj (not its imports) so we only flag what the user wrote themselves.
                root = ProjectRootElement.Open(_context.ProjectFile, pc);
            }
            catch (Exception ex)
            {
                Debug($"WarnOverriddenProperties: could not open {_context.ProjectFile}: {ex.Message}");
                return;
            }

            if (root == null) return;

            var projectPath = _deps.PathMapper.ToBazel(_context.ProjectFile);
            var warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in root.Properties)
            {
                if (!managed.Contains(prop.Name) || !warned.Add(prop.Name))
                    continue;

                BazelLogger.Warning(
                    $"{projectPath}({prop.Location.Line},{prop.Location.Column}): warning BZ0002: " +
                    $"Property '{prop.Name}' is set in the project file but is managed by rules_msbuild and " +
                    $"will be overridden. Remove it from the project file to silence this warning.");
            }
        }

        private bool ValidateTfm(ProjectInstance? project)
        {
            var actualTfm = project?.GetProperty("TargetFramework")?.EvaluatedValue ?? "";
            if (actualTfm != _context.Tfm)
            {
                Error(
                    $"Bazel expected TargetFramework {_context.Tfm}, but {_context.WorkspacePath(_context.ProjectFile)} " +
                    $"is configured to use TargetFramework '{actualTfm}'. Refusing to build as this will " +
                    $"produce unreachable output. Please reconfigure the project and/or BUILD file.");
                return false;
            }

            return true;
        }

        private void EndBuild(BuildResultCode result)
        {
            if (result == BuildResultCode.Success)
                _deps.Cache.Save();

            _buildManager.EndBuild();

            if (_targetGraph != null)
            {
                File.WriteAllText(_context.LabelPath(".dot"), _targetGraph.ToDot());
            }

            if (result != BuildResultCode.Success) return;

            switch (_action)
            {
                case "restore":
                    FixRestoreOutputs(_context.MSBuild.BaseIntermediateOutputPath);
                    break;
                case "build":
                {
                    FixRestoreOutputs(Path.Combine(_context.MSBuild.BaseIntermediateOutputPath,
                        _context.MSBuild.Configuration));
                    if (_context.IsTest)
                    {
                        // todo make this less hacky
                        var loggerPath = Path.Combine(
                            Path.GetDirectoryName(_context.NuGetConfig)!,
                            "packages/junitxml.testlogger/3.0.87/build/_common");
                        var tfmPath = Path.Combine(_context.MSBuild.OutputPath, _context.Tfm);
                        foreach (var dll in Directory.EnumerateFiles(loggerPath))
                        {
                            var filename = Path.GetFileName(dll);
                            File.Copy(dll, Path.Combine(tfmPath, filename));
                        }
                    }

                    if (_context.IsExecutable)
                    {
                        var basename = _context.Bazel.Label.Name;
                        if (Path.DirectorySeparatorChar == '\\')
                        {
                            // there's not a great "IsWindows" method in c#
                            basename += ".exe";
                        }

                        WriteRunfilesInfo(_context.OutputPath(_context.Tfm, "runfiles.info"), $"../{basename}.runfiles",
                            false);
                    }

                    break;
                }
            }
        }

        private void WriteRunfilesInfo(string outputPath, string expectedRelativePath, bool useDirectory)
        {
            var directory = Path.GetDirectoryName(outputPath);
            Directory.CreateDirectory(directory!);
            File.WriteAllLines(outputPath, new string[]
            {
                // first line is the expected location of the runfiles directory from the assembly location
                expectedRelativePath,
                // second line is the origin workspace (nice to have)
                _context.Bazel.Label.Workspace,
                // third is the package (nice to have)
                _context.Bazel.Label.Package,
                // runfiles strategy to use: if we are publishing, no other executable will ever have our runfiles,
                // so we retrieve them from our own directory and ignore other variables
                useDirectory ? "selfish" : "auto"
            });
        }

        /// <summary>
        /// Restore writes absolute paths to project.assets.json and to .props and .targets files.
        /// We can't have absolute paths for these, because they will be re-used in future actions in a different
        /// sandbox or machine.
        /// For the assets file, we assume that the only build action that is looking at the file is MSBuild building
        /// the direct project. As such, the current directory will be the directory of this project file, so we'll
        /// make all the paths relative to the project file.
        /// For the xml files, we'll prepend MSBuildThisFileDirectory in case another project file is evaluating these
        /// files.
        /// </summary>
        private void FixRestoreOutputs(string targetPath)
        {
            targetPath = Path.GetFullPath(targetPath);
            var fixer = new RestoreFixer(_context, new Files(), new Paths());
            Directory.CreateDirectory(targetPath);

            Fix(targetPath);

            void Fix(string path)
            {
                foreach (var directory in Directory.EnumerateDirectories(path))
                    Fix(directory);
                foreach (var ideFileName in Directory.EnumerateFiles(path))
                {
                    // Skip NuGet's UUID-named ".tmp" atomic-write intermediaries: they are not
                    // real restore outputs (and are often left 0-byte when a restore runs in a
                    // sandbox), so there are no absolute paths to rewrite in them.
                    if (Path.GetExtension(ideFileName).Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                        continue;
                    fixer!.Fix(ideFileName);
                }
            }
        }

        private void CopyFiles(string filesKey, string destinationDirectory, bool trimPackage = false)
        {
            // if (!_context.Command.NamedArgs.TryGetValue(filesKey, out var contentListString) ||
            //     contentListString == "") return;
            var contentListString = "";
            var contentList = contentListString.Split(";");
            var createdDirectories = new HashSet<string>();
            foreach (var filePath in contentList)
            {
                var src = new FileInfo(filePath);
                string destinationPath;
                if (filePath.StartsWith("external/"))
                {
                    destinationPath = filePath.Substring("external/".Length);
                }
                else if (trimPackage && filePath.StartsWith(_context.Bazel.Label.Package))
                {
                    destinationPath = filePath.Substring(_context.Bazel.Label.Package.Length + 1);
                }
                else
                {
                    destinationPath = Path.Combine(_context.Bazel.Label.Workspace, filePath);
                }

                var dest = new FileInfo(Path.Combine(destinationDirectory, destinationPath));

                if (!dest.Exists || src.LastWriteTime > dest.LastWriteTime)
                {
                    if (!createdDirectories.Contains(dest.DirectoryName!))
                    {
                        Directory.CreateDirectory(dest.DirectoryName!);
                        createdDirectories.Add(dest.DirectoryName!);
                    }

                    src.CopyTo(dest.FullName, true);
                }
            }
        }

        public void Error(string message) => BazelLogger.Error(_deps.PathMapper.ToBazel(message));

        public void Fail(string shortMessage, string message)
        {
            var projectPath = _deps.PathMapper.ToBazel(_context.ProjectFile);
            var summary = new StringBuilder()
                .AppendLine("__________________________________________________")
                .AppendLine($"Project \"{projectPath}\": (Build target(s)):")
                .AppendLine()
                .AppendLine($"{projectPath}(-1,-1): error BZ0001: {_deps.PathMapper.ToBazel(message)}");
            BazelLogger.Error(summary.ToString());
            /* proper error output from normal build that triggers IDE parsing:
0>__________________________________________________
0>Project "$exec_root/rules_msbuild/eng/tar/tar.csproj" (Build target(s)):
0>
0>$exec_root/rules_msbuild/eng/tar/Program.cs(62,30): Error CS1026 : ) expected
0>Done building project "tar.csproj" -- FAILED.
            */
        }
    }
}