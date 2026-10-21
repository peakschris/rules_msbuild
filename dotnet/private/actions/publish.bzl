load("@bazel_skylib//lib:paths.bzl", "paths")
load("@rules_dotnet_runtime//dotnet:defs.bzl", "DotnetPublishInfo")
load(":common.bzl", "cache_set", "declare_caches", "write_cache_manifest")
load("//dotnet/private:context.bzl", "dotnet_exec_context", "make_builder_cmd")
load("//dotnet/private:providers.bzl", "DotnetLibraryInfo", "MSBuildDirectoryInfo", _DotnetPublishInfo = "DotnetPublishInfo")
load("//dotnet/private/util:util.bzl", "to_manifest_path")

def publish(ctx):
    info = ctx.attr.target[DotnetLibraryInfo]
    restore = info.restore

    dotnet = dotnet_exec_context(ctx, info.executable, False, restore.target_framework)

    output_dir = ctx.actions.declare_directory(paths.join("publish", dotnet.config.tfm))

    launcher = None
    launcher_windows = None

    # if info.executable:
    #     launcher = ctx.actions.declare_file(paths.join("publish", dotnet.config.tfm, restore.assembly_name))
    #     launcher_windows = ctx.actions.declare_file(restore.assembly_name + ".exe", sibling = launcher)

    cache = declare_caches(ctx, "publish")

    caches = info.caches
    cache_manifest = write_cache_manifest(ctx, cache, caches)

    args, cmd_outputs = make_builder_cmd(ctx, dotnet, "publish", restore.directory_info, restore.assembly_name)

    args.add_all(["--launcher_template", ctx.file._launcher_template])

    runfiles_manifest = manual_runfiles(ctx, info)

    inputs = depset(
        [cache_manifest, ctx.file._launcher_template, runfiles_manifest] + info.files.to_list(),
        transitive = [info.runfiles],
    )
    outputs = [output_dir, cache.result, cache.project] + cmd_outputs

    # Honor the default shell environment so operator-declared `--action_env`
    # entries reach MSBuild. This is what lets a NativeAOT (`<PublishAot>`) project
    # pick up the MSVC `PATH`/`LIB`/`INCLUDE` for its native link step, which the
    # publish action would otherwise never see.
    #
    # Hermeticity is preserved by `--incompatible_strict_action_env`: with it set,
    # the inherited environment is deterministic (a static PATH plus exactly the
    # declared `--action_env` vars, never the client's ambient env). The explicit
    # `env = dotnet.env` still wins per key, so the NuGet isolation vars
    # (USERPROFILE/APPDATA/LOCALAPPDATA/ProgramFiles(x86)/PROGRAMFILES) stay pinned.
    # See dotnet/private/msbuild/ENVIRONMENT.md for the full rationale and the csproj
    # change AOT projects need (IlcUseEnvironmentalTools).
    ctx.actions.run(
        mnemonic = "DotnetPublish",
        inputs = inputs,
        outputs = outputs,
        executable = dotnet.sdk.dotnet,
        arguments = [args],
        env = dotnet.env,
        use_default_shell_env = True,
        tools = dotnet.builder.files,
    )

    publish_info = _DotnetPublishInfo(
        output_dir = output_dir,
        files = depset(direct = outputs, transitive = [inputs]),
        caches = cache_set([cache], transitive = [caches]),
        library = info,
        restore = restore,
        runfiles_manifest = runfiles_manifest,
        public = DotnetPublishInfo(
            launcher = launcher,
            launcher_windows = launcher_windows,
            files = depset(outputs),
            output_directory = output_dir,
        ),
    )

    return publish_info

def manual_runfiles(ctx, info):
    runfiles = info.runfiles.to_list()
    runfiles_manifest = ctx.actions.declare_file(ctx.attr.name + ".runfiles_manifest")
    ctx.actions.write(
        runfiles_manifest,
        "\n".join([
            "%s %s" % (to_manifest_path(ctx, r), to_manifest_path(ctx, r, True))
            for r in runfiles
        ]),
    )
    return runfiles_manifest
