# Dotnet Rules for Bazel

| Windows                                                                                                                                                                                                                                                  | Mac                                                                                                                                                                                                                                              | Linux                                                                                                                                                                                                                                                |
| -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [![Build Status](https://dev.azure.com/samhowes/rules_msbuild/_apis/build/status/samhowes.rules_msbuild?branchName=master&jobName=windows)](https://dev.azure.com/samhowes/rules_msbuild/_build/latest?definitionId=6&branchName=master&jobName=windows) | [![Build Status](https://dev.azure.com/samhowes/rules_msbuild/_apis/build/status/samhowes.rules_msbuild?branchName=master&jobName=mac)](https://dev.azure.com/samhowes/rules_msbuild/_build/latest?definitionId=6&branchName=master&jobName=mac) | [![Build Status](https://dev.azure.com/samhowes/rules_msbuild/_apis/build/status/samhowes.rules_msbuild?branchName=master&jobName=linux)](https://dev.azure.com/samhowes/rules_msbuild/_build/latest?definitionId=6&branchName=master&jobName=linux) |

<!--
Links
 -->

> These docs are under construction. Please open an issue for any specific questions!

rules_msbuild is an alternative to [rules_dotnet](https://github.com/bazelbuild/rules_dotnet).

# In Beta!

```bash
# set up a hello world dotnet app
mkdir HelloBazelDotnet && cd HelloBazelDotnet
dotnet new console -o AwesomeExecutable --no-restore
dotnet new sln && dotnet sln add AwesomeExecutable

dotnet tool install -g SamHowes.Bzl     # installs dotnet cli tool `bzl`
bzl                              # automatically generate a WORKSPACE and ide integration files
bazel run //:gazelle             # generate build files with custom Gazelle language
bazel build //...                # use bazel to build .csproj, .fsproj, or .vbproj files
bazel run //AwesomeExecutable    # => Hello World!
```

Check out the `tests/` directory & `e2e/` directory for examples

## Features

1. Build .csproj files with Bazel
1. `dotnet build` feature parity
1. IDE Integration with JetBrains Rider / Visual Studio with no custom plugins
1. Automated BUILD file generation via [bazel gazelle](https://github.com/bazelbuild/bazel-gazelle)
1. Runfiles Library
1. Bazel sandboxing compatible
1. [Grpc & Proto support](./tests/examples/Grpc) Out of the Box via
   [grpc-dotnet](https://github.com/grpc/grpc-dotnet)
1. No third party workspace dependencies

# Contents

1. Overview
    1. [Usage](#usage)
        1. [msbuild_library](#msbuild_library)
        1. [msbuild_binary](#msbuild_binary)
        1. [NuGet Dependencies](#nuget-dependencies)
    1. [Sharp Edges](#watch-out-for-sharp-edges)
    1. [Should I Use rules_msbuild?](#should-i-use-rules_msbuild)
1. [Rules](docs/rules.md)
1. [Understanding the build](docs/Understanding.md)
1. [Build File Generation with Gazelle](gazelle/dotnet/Readme.md)<!-- toc:start -->
1. [Rules](docs/rules.md)<!-- toc:end -->
1. [Implementation Details](docs/ImplementationDetails.md)

# Usage

> Note: [SamHowes.Bzl](https://www.nuget.org/packages/SamHowes.Bzl/) and using
> `bazel run //:gazelle` (as described above) is strongly recommended.

## Workspace

```python
# //WORKSPACE
load("@bazel_tools//tools/build_defs/repo:http.bzl", "http_archive")
http_archive(
    name = "rules_msbuild",
    sha256 = "5bb9d506ae025796a9d4e5dada6408d6cb255c1dc52e1f11e6eb93ffc838f341",
    urls = ["https://github.com/samhowes/rules_msbuild/releases/download/0.0.17/rules_msbuild-0.0.17.tar.gz"],
)
load("@rules_msbuild//dotnet:deps.bzl", "msbuild_register_toolchains", "msbuild_rules_dependencies")

msbuild_rules_dependencies()
# See https://dotnet.microsoft.com/download/dotnet for valid versions
msbuild_register_toolchains(version = "host")
```

## Compiling Assemblies

`msbuild_library` and `msbuild_binary` are macros that compile
[framework dependent](https://andrewlock.net/should-i-use-self-contained-or-framework-dependent-publishing-in-docker-images/)
assemblies that can be run with `dotnet run`. The macros define the targets `<name>_restore`,
`<name>`, and `<name>_publish` labels.

Given a .csproj file located at //console:console.csproj with a TargetFramework of net10.0, invoking
`bazel build //console/console_publish` will result in
`bazel-bin/console/publish/net10.0/console.dll` that can be run with `dotnet exec console.dll`, and
`bazel run //console` will run the executable under all the standard bazel expectations.

The `//:gazelle` rule generated by `samhowes.bzl` will generate all the necessary build files from
any .csproj, .fsproj, or .vbproj files that you have in your repository.

### msbuild_library

```python
# //ClassLibrary/BUILD
load("@rules_msbuild//dotnet:defs.bzl", "msbuild_library")

# expects ClassLibrary.csproj to exist
msbuild_library(
    name = "ClassLibrary",
    srcs = ["Utility.cs"],                  # srcs can be explicitly specified
    target_framework = "netstandard2.1",
    deps = [
        "@nuget//NewtonSoft.Json",
    ],
)
```

### msbuild_binary

```python
# //Console/BUILD
load("@rules_msbuild//dotnet:defs.bzl", "msbuild_binary")

msbuild_binary(
    name = "hello",                        # adds the property AssemblyName="hello"
    # omitting srcs automatically globs the directory for source files
    project_file = "Console.csproj",       # project_file is specified when AssemblyName is different
    target_framework = "net10.0",
    deps = [
        "//ClassLibrary",
        "@nuget//NewtonSoft.Json",
    ],
)
```

### NuGet dependencies

`@rules_msbuild//gazelle/dotnet` automatically manages your nuget dependencies by parsing your
project files.

NuGet packages are represented by a PackageId and a list of frameworks that must be restored for
that PackageId. Multiple nuget package versions can be specified.

```python
# //WORKSPACE
load("//deps:nuget.bzl", "nuget_deps")
nuget_deps()
```

```python
# //deps:nuget.bzl
# Configure dotnet SDK.
dotnet_sdk = use_extension("@rules_msbuild//dotnet:extensions.bzl", "dotnet")
dotnet_sdk.toolchain(
    name = "dotnet_sdk_net10",
    version = "<dotnet_sdk_version>",   # <-- Use correct dotnet SDK version (e.g. "10.0.103")
    nuget_repo = "@nuget_deps//:tfm_mapping",
    shas = {
        "windows_amd64": "<dotnet_sdk_sha>",   # <-- Update with sha256 for desired SDK platform/version
    },
)

use_repo(dotnet_sdk, "dotnet_sdk_net10")
register_toolchains("@dotnet_sdk_net10//:all")

# Nuget extension setup. Add all required NuGet packages for your project.
nuget_deps = use_extension("@rules_msbuild//dotnet:extensions.bzl", "nuget")
nuget_deps.fetch(
    name = "nuget_deps",
    target_frameworks = [
        "<target_framework>",  # e.g. "net10.0"
    ],
    dotnet_sdk_root = "@dotnet_sdk_net10//:ROOT",
    packages = {
        # Add the NuGet packages your .csproj files need:
        # "PackageName/Version": ["net10.0"],
        # Examples:
        # "Newtonsoft.Json/13.0.3": ["net10.0"],
        # "NLog/6.0.5": ["net10.0"],
        # "Microsoft.Data.SqlClient/6.1.3": ["net10.0"],
        # Add any additional packages from your .csproj's NuGet requirements!
    },
    package_sources = [
        # Add your custom nuget package repositories here
        '{"key": "artifactory", "value": "https://artifacts.industrysoftware.automation.siemens.com/artifactory/api/nuget/nuget/"}'  
    ],
    use_host = False,
)

use_repo(nuget_deps, "nuget_deps")
```



## Migration Guide: Migrating `rules_msbuild` from net10 to netXY

This README provides all the steps required to migrate the [`rules_msbuild`](https://github.com/peakschris/rules_msbuild/) Bazel integration from .NET 10 (`net10`) to .NET 12 (`netXY`).  

---

### A. Setup SamHowes.Microsoft.Build

1. **Clone the Repository**

   ```bash
   git clone https://github.com/peakschris/SamHowes.Microsoft.Build
   cd SamHowes.Microsoft.Build
   ```

2. **Update MSBuild Version**  
Edit build.sh and build.bat to replace any net10-compatible MSBuild versions with the netXY-compatible MSBuild version.  
Check .NET releases for the correct MSBuild version for netXY.


3. **Build SamHowes.Microsoft.Build packages**

   - Run the following commands to build the packages:

     ```bash
     bash ./build.sh         # For Linux/macOS
     ```
     ```bash
     ./build.bat         # For Windows
     ```



4. **Fix Runtime Issues**  
Resolve runtime errors to build SamHowes.Microsoft.Build and SamHowes.Microsoft.Build.Framework


5. **Release Packages on GitHub**  
Upload build artifacts/nupkg files to github releases.   


### B. Update rules_msbuild

1. Clone the Repository
    ```
    git clone https://github.com/peakschris/rules_msbuild/
    cd rules_msbuild
    ```

2. Replace net10.0 substrings with netXY.0 with help of VS Code


3. Update all usages, including in MODULE.bazel, build scripts, and configs, to netXY.0.


4. Update MODULE.bazel for netXY SDK
Update SDK version/sha for netXY:

    ```
    dotnet_sdk.toolchain(
        name = "dotnet_sdk_netXY",
        version = "12.0.103",   # Example .NET 12 version
        shas = {
            "windows_amd64": "<netXY_sdk_sha>",   # Fill with netXY sdk sha256
        },
    )
    ```

5. Update NuGet package versions as needed for netXY compatibility.


6. Fix Runtime Issues

7. Build and test with Bazel.  
Address build failures, dependency mismatches, and runtime exceptions.
```
bazel test //tests/..
```


# Watch out for sharp edges!

These rules are still in "beta" and the core functionality is still being refined.

These rules assume you have used `dotnet tool install -g samhowes.bzl` to set up your workspace and
run `bazel run //:gazelle` after adding any source files, nuget packages, or project references.

Any issues with the label
[sharp-edge](https://github.com/samhowes/rules_msbuild/issues?q=is%3Aissue+is%3Aopen+label%3Asharp-edge)
are specifically known to be confusing and make working with these rules hard.

Specifically:
1. .NET 6 is required to run rules_msbuild. You can still compile old frameworks though. This is
   because rules_msbuild uses MSBuild 17 to build .NET 6 code, and MSBuild 17 targets net10.0 as a
   TargetFramework.

3. If a machine doesn't have a dotnet sdk/runtime installed, and a project file targets a framework
   version defined by that sdk/runtime, then a
   [weird error message](https://github.com/samhowes/rules_msbuild/issues?q=is%3Aissue+is%3Aopen+label%3Asharp-edge)
   will be output by the NuGetparser, or the builder when restoring packages for that framework
   version.

# Should I Use rules_msbuild?

rules_msbuild works as intended (see //tests/... and //e2e:all), and the implementation on .NET 5 & MSBuild 16 has survived a [major version upgrade to .NET 6 and MSBuild 17](https://github.com/samhowes/rules_msbuild/pull/198). [rules_tsql](https://github.com/samhowes/rules_tsql) is a small project that is built and tested using rules_msbuild.

rules_msbuild is not yet optimized for performance, nor tested for performance. Specifically, because of the sandboxed execution model, [shared compilation is disabled](https://github.com/samhowes/rules_msbuild/issues/35), which likely leads to significant [performance degredation](https://github.com/dotnet/roslyn/issues/12360#issuecomment-233473465) for larger builds.

Implementation has started on [bazel persistent workers](https://github.com/samhowes/rules_msbuild/issues/201) which should allow for shared compilation as well as more efficient MSBuild performance as build results could be kept in memory instead of loading from disk. Based on [comments from Microsoft engineers](https://github.com/dotnet/roslyn/issues/12360#issuecomment-233473465) this could result in a 3x performance boost, as indicated, no performance testing has been done yet.

rules_msbuild could be integrated with bazelbuild/rules_dotnet in the future. See the [discussion issue for more details](https://github.com/bazelbuild/rules_dotnet/issues/260).
