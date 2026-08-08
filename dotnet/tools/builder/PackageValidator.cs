#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Build.Execution;

namespace RulesMSBuild.Tools.Builder
{
    /// <summary>
    /// Validates that every NuGet &lt;PackageReference&gt; resolved during restore was explicitly
    /// declared in the Bazel target's <c>deps</c> attribute.
    ///
    /// The builder receives a JSON file listing the declared package names (written by restore.bzl).
    /// After restore, this class compares that list against the <c>project.assets.json</c> produced
    /// by NuGet to find undeclared packages and fail with an actionable error message.
    /// </summary>
    public class PackageValidator
    {
        private readonly BuildContext _context;

        public PackageValidator(BuildContext context)
        {
            _context = context;
        }

        public BuildResultCode Validate()
        {
            if (string.IsNullOrEmpty(_context.DeclaredPackagesFile))
                return BuildResultCode.Success;

            var assetsPath = Path.Combine(
                _context.MSBuild.BaseIntermediateOutputPath, "project.assets.json");

            if (!File.Exists(assetsPath))
                return BuildResultCode.Success;

            // Build a case-insensitive set of explicitly declared package names.
            var declaredJson = File.ReadAllText(_context.DeclaredPackagesFile);
            var declaredList = JsonSerializer.Deserialize<List<string>>(declaredJson)
                               ?? new List<string>();
            var declared = new HashSet<string>(declaredList, StringComparer.OrdinalIgnoreCase);

            // Parse project.assets.json and collect direct package references that are NOT
            // auto-referenced (auto-referenced packages come from the .NET SDK framework
            // implicit deps and do not need to be listed in Bazel deps).
            var missing = new List<string>();
            using var stream = File.OpenRead(assetsPath);
            var assets = JsonSerializer.Deserialize<JsonElement>(stream);

            if (assets.TryGetProperty("project", out var project) &&
                project.TryGetProperty("frameworks", out var frameworks))
            {
                foreach (var framework in frameworks.EnumerateObject())
                {
                    if (!framework.Value.TryGetProperty("dependencies", out var deps))
                        continue;

                    foreach (var dep in deps.EnumerateObject())
                    {
                        // Skip packages that are implicitly added by the .NET SDK
                        // (autoReferenced=true in project.assets.json).
                        if (dep.Value.TryGetProperty("autoReferenced", out var auto) &&
                            auto.GetBoolean())
                            continue;

                        if (!declared.Contains(dep.Name))
                            missing.Add(dep.Name);
                    }
                }
            }

            if (missing.Count == 0)
                return BuildResultCode.Success;

            Console.Error.WriteLine(
                $"ERROR [{_context.Bazel.Label}]: The following NuGet packages are referenced in " +
                $"the project file but are not declared in the Bazel `deps` attribute:");
            foreach (var pkg in missing.Distinct().OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                Console.Error.WriteLine($"  - {pkg}  →  add \"@nuget//{pkg}\" to `deps`");

            return BuildResultCode.Failure;
        }
    }
}
