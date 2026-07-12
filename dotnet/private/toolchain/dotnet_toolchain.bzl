load("//dotnet/private:platforms.bzl", "PLATFORMS")
load("//dotnet/private:providers.bzl", "DotnetLibraryInfo", "DotnetSdkInfo")

def _sdk_make_variables(sdk):
    """Make-variables exported by both the dotnet toolchain and current_dotnet_toolchain.

    BAZEL_DOTNET_SDKROOT points at the versioned SDK dir (absolute, prefixed with
    the output_base). DOTNET_BIN/DOTNET point at the `dotnet`/`dotnet.exe` launcher
    (sdk.dotnet) as an execroot-relative exec-path (File.path, e.g.
    "external/<repo>/dotnet.exe") -- deliberately mirroring how the nodejs
    toolchain defines NODE_PATH -- so bazel_env can expose `dotnet` on PATH via
    `tools = {"dotnet": "$(DOTNET_BIN)"}`. It must NOT be made absolute: bazel_env
    resolves the value as a runfiles rlocation (stripping the leading "external/"),
    and an absolute path defeats that lookup.
    """
    dotnet_bin = sdk.dotnet.path
    return {
        "BAZEL_DOTNET_SDKROOT": "/".join([sdk.config.trim_path, sdk.sdk_root.path]),
        "DOTNET_BIN": dotnet_bin,
        "DOTNET": dotnet_bin,
    }

def _dotnet_toolchain_impl(ctx):
    sdk = ctx.attr.sdk[DotnetSdkInfo]
    cross_compile = ctx.attr.dotnetos != sdk.dotnetos or ctx.attr.dotnetarch != sdk.dotnetarch
    builder_info = ctx.attr.builder[DotnetLibraryInfo]
    return [
        platform_common.ToolchainInfo(
            name = ctx.label.name,
            cross_compile = cross_compile,
            default_dotnetos = ctx.attr.dotnetos,
            default_dotnetarch = ctx.attr.dotnetarch,
            sdk = sdk,
            _builder = struct(
                # assembly is now a TreeArtifact (publish/ directory) capturing all publish outputs.
                assembly = builder_info.assembly,
                # exe_path is the string path to the .dll inside the TreeArtifact.
                exe_path = builder_info.exe_path,
                files = depset(
                    # Include the publish TreeArtifact so all publish outputs (runtimeconfig.json,
                    # deps.json, etc.) are available in the sandboxes of downstream actions.
                    [builder_info.assembly],
                    transitive = [sdk.runfiles],
                ),
            ),
        ),
        platform_common.TemplateVariableInfo(_sdk_make_variables(sdk)),
    ]

dotnet_toolchain = rule(
    _dotnet_toolchain_impl,
    attrs = {
        "builder": attr.label(
            cfg = "exec",
            doc = "Tool used to execute most Dotnet actions",
        ),
        "dotnetos": attr.string(
            mandatory = True,
            doc = "Default target OS",
        ),
        "dotnetarch": attr.string(
            mandatory = True,
            doc = "Default target architecture",
        ),
        "sdk": attr.label(
            mandatory = True,
            providers = [DotnetSdkInfo],
            cfg = "exec",
            doc = "The SDK this toolchain is based on",
        ),
    },
    doc = "Defines a Dotnet toolchain based on an SDK",
)

def _current_dotnet_toolchain_impl(ctx):
    toolchain = ctx.toolchains["@rules_msbuild//dotnet:toolchain"]
    sdk = toolchain.sdk
    return [
        toolchain,
        # DefaultInfo.files becomes the runfiles for `$(DOTNET_BIN)` under bazel_env
        # (it uses this depset, not default_runfiles). Include the whole SDK tree
        # (sdk.runfiles -- "files required to run a command with the sdk"), not just
        # the launcher: dotnet.exe is a muxer that locates the runtime/SDK relative
        # to its own on-disk location, so its siblings must be materialized
        # alongside it. All files come from the single SDK repo, so bazel_env's
        # single-repo constraint still holds.
        DefaultInfo(files = depset([sdk.dotnet], transitive = [sdk.runfiles])),
        # Re-export the SDK make-variables so a consumer that puts this target in
        # `toolchains = {...}` (bazel_env) can expand $(DOTNET_BIN)/$(DOTNET)/
        # $(BAZEL_DOTNET_SDKROOT). Mirrors @rules_rust//rust/toolchain:current_rust_toolchain.
        platform_common.TemplateVariableInfo(_sdk_make_variables(sdk)),
    ]

current_dotnet_toolchain = rule(
    _current_dotnet_toolchain_impl,
    toolchains = ["@rules_msbuild//dotnet:toolchain"],
    doc = (
        "Exposes the resolved dotnet toolchain as a normal target: its DefaultInfo " +
        "carries the `dotnet` launcher and it provides the DOTNET_BIN/DOTNET/" +
        "BAZEL_DOTNET_SDKROOT make-variables. Consume it from bazel_env " +
        "(toolchains = {\"dotnet\": \"@rules_msbuild//dotnet:current_dotnet_toolchain\"}) " +
        "to put `dotnet` on PATH, the way rules_rust's current_rust_toolchain does for rust."
    ),
)

def declare_toolchains(host, sdk, builder):
    """Declares dotnet_toolchain and toolchain targets for each platform."""

    # keep in sync with generate_toolchain_names
    host_dotnetos, _, host_dotnetarch = host.partition("_")
    for p in PLATFORMS:
        toolchain_name = "dotnet_" + p.name
        impl_name = toolchain_name + "-impl"

        constraints = p.constraints

        dotnet_toolchain(
            name = impl_name,
            dotnetos = p.dotnetos,
            dotnetarch = p.dotnetarch,
            sdk = sdk,
            builder = builder,
            tags = ["manual"],
            visibility = ["//visibility:public"],
        )
        native.toolchain(
            name = toolchain_name,
            toolchain_type = "@rules_msbuild//dotnet:toolchain",
            exec_compatible_with = [
                "@rules_msbuild//dotnet:" + host_dotnetos,
                "@rules_msbuild//dotnet:" + host_dotnetarch,
            ],
            target_compatible_with = constraints,
            toolchain = ":" + impl_name,
        )
