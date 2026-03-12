load("@rules_go//go:def.bzl", "go_test")
load("@rules_msbuild//dotnet:defs.bzl", "msbuild_binary")

msbuild_binary(
    name = "Greeter",
    srcs = ["Greeter.cs"],
    project_file = "Greeter.csproj",
    target_framework = "net10.0",
    visibility = ["//visibility:public"],
)

genrule(
    name = "run_greeter",
    srcs = [],
    outs = ["run_greeter.txt"],
    cmd = "$(location :Greeter) 'genrule' > $@",
    tools = [":Greeter"],
)

go_test(
    name = "launcher_test",
    size = "small",
    srcs = ["launcher_test.go"],
    data = [
        ":Greeter",
        ":run_greeter",
    ],
    deps = [
        "//tests/tools/executable",
        "//tests/tools/files",
        "@com_github_stretchr_testify//assert",
    ],
)
