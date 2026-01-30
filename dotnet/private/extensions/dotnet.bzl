load("@rules_msbuild//deps:public_nuget.bzl", "FRAMEWORKS", "PACKAGES")
load("@rules_msbuild//dotnet:defs.bzl", "nuget_deps_helper", "nuget_fetch")
load("@rules_msbuild//dotnet/private/toolchain:sdk.bzl", "dotnet_download_sdk")

toolchain = tag_class(
    doc = "Register dotnet SDK toolchain",
    attrs = {
        "name": attr.string(default = "dotnet_sdk"),
        "version": attr.string(doc = "The dotnet sdk version to use, or 'host' to use the host machine's installed sdk"),
        "shas": attr.string_dict(doc = "A map of sha256 checksums for downloading the sdk"),
        "nuget_repo": attr.label(doc = "The nuget repository name to use for fetching packages", default = Label("@nuget//:tfm_mapping")),
    },
)

def dotnet_impl(mctx):
    for mod in mctx.modules:
        for toolchain in mod.tags.toolchain:
            print("Registering dotnet sdk toolchain: %s" % toolchain.version)
            dotnet_download_sdk(
                name = toolchain.name,
                version = toolchain.version,
                shas = toolchain.shas,
                nuget_repo = toolchain.nuget_repo,
            )
    # The type and attributes of repositories created by this extension are fully deterministic
    # and thus don't need to be included in MODULE.bazel.lock.
    # Note: This ignores get_m2local_url, but that depends on local information and environment
    # variables only. In fact, since it depends on the host OS, *not* including the extension
    # result in the lockfile makes it more portable across different machines.
    return mctx.extension_metadata(reproducible = True)

dotnet = module_extension(
    dotnet_impl,
    tag_classes = {
        "toolchain": toolchain,
    },
)
