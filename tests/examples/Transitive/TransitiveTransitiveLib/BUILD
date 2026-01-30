load("//tests/tools/build_test:build_test.bzl", "build_test")
load("@rules_msbuild//dotnet:defs.bzl", "msbuild_library")

msbuild_library(
    name = "TransitiveTransitiveLib",
    srcs = ["TransitiveTransitiveLibClass.cs"],
    project_file = "TransitiveTransitiveLib.csproj",
    target_framework = "net8.0",
    visibility = ["//tests/examples/Transitive/TransitiveLib:__pkg__"],
)

build_test(
    name = "TransitiveTransitiveLib_test",
    expected_files = {
        "": [
            "TransitiveTransitiveLib.cache",
        ],
        "net8.0": [
            "TransitiveTransitiveLib.pdb",
            "TransitiveTransitiveLib.dll",
        ],
    },
)
