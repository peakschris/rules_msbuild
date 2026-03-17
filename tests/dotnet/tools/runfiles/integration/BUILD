load("@rules_go//go:def.bzl", "go_test")
load("//tests/tools/build_test:build_test.bzl", "build_test")
load("//dotnet:defs.bzl", "msbuild_binary")

msbuild_binary(
    name = "DotnetCat",
    srcs = ["DotnetCat.cs"],
    data = ["foo.txt"],
    target_framework = "net10.0",
    visibility = ["//visibility:public"],
    deps = [
        "//dotnet/tools/Runfiles",
    ],
)

build_test(
    name = "DotnetCat_publish_test",
    expected_files = {
        "publish/net10.0/DotnetCat.dll.runfiles": [
            "MANIFEST",
        ],
        "publish/net10.0/DotnetCat.dll.runfiles/_main/tests/dotnet/tools/runfiles/integration": [
            "foo.txt",
        ],
    },
    expected_output = "Hello Runfiles!\n",
)

genrule(
    name = "run_dotnet_cat",
    srcs = [],
    outs = ["run_dotnet_cat_result.txt"],
    cmd = "$(location :DotnetCat) > $@",
    tools = [":DotnetCat"],
)

go_test(
    name = "integration_test",
    size = "small",
    srcs = ["dotnet_cat_test.go"],
    data = [
        ":DotnetCat",
        ":run_dotnet_cat",
    ],
    deps = [
        "//tests/tools/executable",
        "//tests/tools/files",
        "@com_github_stretchr_testify//assert",
    ],
)
