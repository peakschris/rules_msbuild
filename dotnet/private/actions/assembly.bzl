load("//dotnet/private:providers.bzl", "DotnetLibraryInfo", "DotnetRestoreInfo", "MSBuildDirectoryInfo")
load("//dotnet/private:context.bzl", "make_builder_cmd")
load(":common.bzl", "cache_set", "declare_caches", "get_nuget_files", "write_cache_manifest")
load("@bazel_skylib//lib:paths.bzl", "paths")
load("@rules_proto//proto:defs.bzl", "ProtoInfo")

def build_assembly(ctx, dotnet):
    restore = ctx.attr.restore[DotnetRestoreInfo]

    # Declare the entire output directory as a TreeArtifact for all targets (executable and library).
    # MSBuild writes deps.json, runtimeconfig.json, and other files alongside the primary DLL into
    # the output directory. Capturing all of them is necessary so that downstream `dotnet publish`
    # actions (which run with NoBuild=true and copy from this path) find the expected files in the
    # sandbox. Using a plain declare_file for the DLL alone caused MSB3030 errors because
    # deps.json was absent from the publish sandbox.
    assembly = ctx.actions.declare_directory(dotnet.config.output_dir_name)

    # Under `bazel coverage`, the builder forces portable PDBs (see MSBuildContext.cs); MSBuild
    # writes <assembly>.pdb *inside* this output directory. Because `assembly` is declared as a
    # TreeArtifact, the PDB is captured automatically as part of that tree -- do NOT declare it as
    # a separate file output. Doing so collides with the TreeArtifact ("output path is a prefix of
    # the other"). coverlet finds the PDB next to the dll via the tree at test runtime.

    # intermediate_dir is a TreeArtifact; declaring a file inside it would conflict, so we only
    # declare the directory. MSBuild will write the intermediate .dll there as well.
    intermediate_dir = ctx.actions.declare_directory(paths.join("obj", dotnet.config.tfm))

    cache = declare_caches(ctx, "build")
    files, caches, runfiles = _process_deps(ctx, dotnet)
    caches = cache_set(transitive = caches)
    cache_manifest = write_cache_manifest(ctx, cache, caches)
    args, cmd_outputs = make_builder_cmd(ctx, dotnet, "build", restore.directory_info, restore.assembly_name)

    # Coverity C# capture (see servicemesh plan): the builder serializes a self-contained,
    # restore-suppressed `dotnet build` response file for this project. bb's live cov-build pass
    # replays `cov-build -- dotnet build @<rsp>` outside Bazel so MSBuild spawns a discrete csc
    # child that Coverity's process monitor recognizes -- the `--bazel` replay path cannot see the
    # in-process builder.dll compile. Surfaced via the `coverity` output group, never built by
    # default. See dotnet/tools/builder/Builder.cs WriteCoverityRsp().
    coverity_rsp = ctx.actions.declare_file(ctx.attr.name + ".coverity.rsp")
    args.add_all(["--coverity_rsp", coverity_rsp])

    # bb's live Coverity C# pass replays this rsp with a standalone `dotnet build` OUTSIDE Bazel, so
    # every file that build action consumes -- restore metadata (project.assets.json, nuget.g.props),
    # the referenced NuGet package assemblies, and dependency project DLLs -- must exist on local disk
    # at replay time. Those are INPUTS to this action (produced by the separate _restore target / deps),
    # never outputs of this target, so a remote cache hit on `--output_groups=coverity` would otherwise
    # materialize the rsp alone and the replay's `dotnet build` would fail at NuGet resolution. Bundling
    # the full input closure into the `coverity` output group (below, via the rule impls) forces Bazel to
    # download all of it as top-level outputs. Kept OUT of `outputs`/DotnetLibraryInfo.files so it never
    # leaks into dependents' runfiles.

    protos = _getProtos(ctx)

    inputs = depset(
        [cache_manifest, ctx.file.project_file] + ctx.files.srcs + ctx.files.content,
        transitive = files + [restore.files] + protos,
    )

    outputs = [
        assembly,
        ctx.actions.declare_directory("restore/_/" + dotnet.config.configuration),
        ctx.actions.declare_directory("restore/" + dotnet.config.configuration),
        intermediate_dir,
        cache.project,
        cache.result,
    ] + cmd_outputs

    # coverity_rsp is a declared output the action always produces, but it is kept out of `outputs`
    # (the `all` output group / DotnetLibraryInfo.files) so it never leaks into dependents' runfiles.
    # It is surfaced only through the dedicated `coverity` output group in the rule impls.
    ctx.actions.run(
        mnemonic = "MSBuild",
        inputs = inputs,
        outputs = outputs + [coverity_rsp],
        executable = dotnet.sdk.dotnet,
        arguments = [args],
        env = dotnet.env,
        tools = dotnet.builder.files,
    )

    info = DotnetLibraryInfo(
        assembly = assembly,
        output_dir = assembly,
        files = depset(direct = outputs, transitive = [inputs]),
        caches = cache_set([cache], transitive = [caches]),
        # Dependency PDBs ride along inside each dependency's output-dir TreeArtifact, which the
        # transitive `runfiles` already pulls in, so they land next to their .dll in the test's
        # runfiles tree where coverlet looks for them.
        runfiles = depset(
            ctx.files.data,
            transitive = runfiles,
        ),
        project_cache = cache.project,
        restore = restore,
        executable = dotnet.config.is_executable,
    )

    # The coverity output group carries the rsp PLUS the full input closure (see the comment at the
    # coverity_rsp declaration): srcs, project file, restore metadata, NuGet package assemblies, and
    # dependency DLLs -- everything the standalone `dotnet build @rsp` replay reads. Requesting
    # `--output_groups=coverity` then forces Bazel to materialize all of it locally even on a remote
    # cache hit, where the default output set (assembly only) would leave the replay's inputs undownloaded.
    coverity_outputs = depset([coverity_rsp], transitive = [inputs])

    return info, outputs, coverity_outputs

def _getProtos(ctx):
    deps = []
    for p in ctx.attr.protos:
        info = p[ProtoInfo]
        deps.append(depset(info.direct_sources, transitive = [info.transitive_sources]))
    return deps

def _process_deps(ctx, dotnet):
    files = []
    caches = []
    runfiles = []

    for d in dotnet.config.implicit_deps:
        get_nuget_files(d, dotnet.config.tfm, files)

    for d in ctx.attr.deps:
        if DotnetLibraryInfo in d:
            info = d[DotnetLibraryInfo]
            files.append(info.files)
            runfiles.append(info.runfiles)
            caches.append(info.caches)

    return files, caches, runfiles
