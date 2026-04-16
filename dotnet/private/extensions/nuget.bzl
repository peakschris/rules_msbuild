load("@rules_msbuild//deps:public_nuget.bzl", "FRAMEWORKS", "PACKAGES")
load("@rules_msbuild//dotnet:defs.bzl", "nuget_deps_helper", "nuget_fetch")

fetch = tag_class(
    doc = "Fetch nuget packages",
    attrs = {
        "name": attr.string(default = "nuget"),

        # Actual artifacts and overrides
        "packages": attr.string_list_dict(),
        "target_frameworks": attr.string_list(doc = "dotnet frameworks", allow_empty = True),

        "test_logger": attr.string(
            default = "JunitXml.TestLogger/3.0.87",
        ),

        "use_host": attr.bool(doc = "When false (default) nuget packages will be fetched into a bazel-only directory, when true, the " +
                   "host machine's global packages folder will be used. This is determined by executing " +
                   "`dotnet nuget locals global-packages --list", default = False),
        "dotnet_sdk_root": attr.label(
            doc = "Set this to @@dotnet_sdk//:ROOT",
        ),
        "package_sources": attr.string_list(
            doc = "Additional nuget package sources to use when fetching packages'. For example: " +
                  '{"key": "nuget.org", "value": "https://api.nuget.org/v3/index.json", "protocolVersion": "3"}',
            default = [],
        ),
        "credential_helper_windows": attr.string(
            doc = "Path to the credential helper executable on Windows.",
            default = "",
        ),
        "credential_helper_linux": attr.string(
            doc = "Path to the credential helper executable on Linux/macOS.",
            default = "",
        ),
    },
)

def nuget_impl(mctx):
    for mod in mctx.modules:
        for fetch in mod.tags.fetch:
            nuget_fetch(
                name = fetch.name,
                packages = fetch.packages,
                target_frameworks = fetch.target_frameworks,
                test_logger = fetch.test_logger,
                use_host = fetch.use_host,
                deps = nuget_deps_helper(FRAMEWORKS, PACKAGES),
                dotnet_sdk_root = fetch.dotnet_sdk_root,
                package_sources = fetch.package_sources,
                credential_helper_windows = fetch.credential_helper_windows,
                credential_helper_linux = fetch.credential_helper_linux,
            )
    # The type and attributes of repositories created by this extension are fully deterministic
    # and thus don't need to be included in MODULE.bazel.lock.
    # Note: This ignores get_m2local_url, but that depends on local information and environment
    # variables only. In fact, since it depends on the host OS, *not* including the extension
    # result in the lockfile makes it more portable across different machines.
    return mctx.extension_metadata(reproducible = True)

nuget = module_extension(
    nuget_impl,
    tag_classes = {
        "fetch": fetch,
    },
)
