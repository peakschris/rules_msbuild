load("@bazel_skylib//:bzl_library.bzl", "bzl_library")
load("//dotnet/private/toolchain:dotnet_toolchain.bzl", "current_dotnet_toolchain")
load(":toolchains.bzl", "declare_constraints")

package(default_visibility = ["//visibility:public"])

toolchain_type(
    name = "toolchain",
    visibility = ["//visibility:public"],
)

# Resolved dotnet toolchain surfaced as a plain target: carries the `dotnet`
# launcher in DefaultInfo and the DOTNET_BIN/DOTNET/BAZEL_DOTNET_SDKROOT
# make-variables, so tools like bazel_env can put `dotnet` on PATH. Mirrors
# @rules_rust//rust/toolchain:current_rust_toolchain.
current_dotnet_toolchain(
    name = "current_dotnet_toolchain",
    visibility = ["//visibility:public"],
)

declare_constraints()

exports_files(
    ["defs.bzl"],
    visibility = ["//docs:__subpackages__"],
)

bzl_library(
    name = "bzl",
    srcs = glob(["*.bzl"]),
    deps = [
        "//dotnet/private:bzl",
        "//dotnet/private/rules:bzl",
        "//dotnet/private/toolchain:bzl",
    ],
)
