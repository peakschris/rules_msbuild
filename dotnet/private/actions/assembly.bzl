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
    # writes <assembly>.pdb next to the dll in the bazel-bin output dir. Declare it so Bazel tracks
    # and propagates it (coverlet needs it next to the dll at test runtime to map IL -> source).
    pdb = None
    if ctx.configuration.coverage_enabled:
        pdb = ctx.actions.declare_file(paths.join(dotnet.config.output_dir_name, restore.assembly_name + ".pdb"))

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
    if pdb != None:
        outputs.append(pdb)

    ctx.actions.run(
        mnemonic = "MSBuild",
        inputs = inputs,
        outputs = outputs,
        executable = dotnet.sdk.dotnet,
        arguments = [args],
        env = dotnet.env,
        tools = dotnet.builder.files,
    )

    info = DotnetLibraryInfo(
        assembly = assembly,
        output_dir = assembly,
        pdb = pdb,
        files = depset(direct = outputs, transitive = [inputs]),
        caches = cache_set([cache], transitive = [caches]),
        # Carry the PDB as a runtime file so a dependency's .pdb lands next to its .dll in the
        # test's runfiles tree, where coverlet looks for it. transitive `runfiles` already pulls
        # in dependency PDBs the same way.
        runfiles = depset(
            ctx.files.data + ([pdb] if pdb != None else []),
            transitive = runfiles,
        ),
        project_cache = cache.project,
        restore = restore,
        executable = dotnet.config.is_executable,
    )

    return info, outputs

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
