# Build environment: what rules_msbuild changes, and why

A `rules_msbuild` build does **not** run `dotnet` in your normal shell environment. It
deliberately overrides a set of environment variables before invoking the SDK, so builds are
hermetic and reproducible instead of depending on whatever is installed/configured on the machine.
This document explains exactly which variables are overridden, why, and the one place it visibly
diverges from a plain `dotnet build`/`publish`/`run` — so project authors aren't surprised.

## Environment variables rules_msbuild overrides ("hijacks")

Every action builds its environment from `dotnet.env`
(`dotnet/private/context.bzl` -> `_make_env` + `isolated_environment`). On Windows the following
variables are **redirected to the dotnet SDK root** by `isolated_environment()`
(`dotnet/private/msbuild/environment.bzl`):

| Variable            | What the tooling uses it for                                             | Why we isolate it |
|---------------------|--------------------------------------------------------------------------|-------------------|
| `USERPROFILE`       | NuGet global packages folder (`%USERPROFILE%\.nuget\packages`)           | Keep restore inside Bazel-fetched packages, not your shared global cache |
| `APPDATA`           | User-level `NuGet.Config` (`%APPDATA%\NuGet\NuGet.Config`)               | Ignore per-developer feeds/credentials |
| `LOCALAPPDATA`      | NuGet HTTP cache + plugins cache                                          | Avoid machine-local mutable cache state |
| `ProgramFiles(x86)` | Machine-wide `NuGet.Config` (`%ProgramFiles(x86)%\NuGet\Config`)         | Ignore machine-wide feeds |
| `PROGRAMFILES`      | Fallback for machine-wide NuGet config                                    | Same as above |

These map to NuGet's "special folders" (mirrors `NuGetEnvironment.cs` in NuGet.Client). Pointing
them all at a controlled directory forces NuGet to see **only** the Bazel-provided `NuGet.Build.Config`
and the Bazel-fetched packages — nothing from your user profile or the machine. This is the NuGet-layer
equivalent of Bazel's `--incompatible_strict_action_env` scrubbing `PATH`.

Also set (not machine-isolation, just SDK behavior): `DOTNET_CLI_HOME`, `DOTNET_CLI_TELEMETRY_OPTOUT`,
`DOTNET_MULTILEVEL_LOOKUP=0`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_NOLOGO`, `OS`,
`RULES_MSBUILD_VERSION`.

## Why this collides with NativeAOT (`<PublishAot>`)

`ProgramFiles(x86)` is **overloaded**. NuGet uses it for machine-wide config; the MSVC/VS toolchain
*also* uses it — NativeAOT's `Microsoft.NETCore.Native.Windows.targets` runs `findvcvarsall.bat`, which
locates `vswhere` at `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe`. Because we
redirect `ProgramFiles(x86)` for NuGet's sake, vswhere can't be found, VS discovery fails, and the
publish fails with:

> error : Platform linker not found. Ensure you have all the required prerequisites ...

The native linker discovery is collateral damage of the (intentional) NuGet isolation.

## How env vars reach the publish action

Historically none of the build actions set `use_default_shell_env`, so `--action_env` was ignored —
the explicit `env = dotnet.env` dict was the entire environment. The **publish** action
(`dotnet/private/actions/publish.bzl`) now sets `use_default_shell_env = True`. With
`--incompatible_strict_action_env` (standard in hermetic setups) this inherits a *deterministic* set —
a static `PATH` plus exactly the declared `--action_env` entries, never your ambient shell — and
`env = dotnet.env` still wins per key, so the NuGet isolation vars above stay pinned. This lets a
NativeAOT project receive the MSVC `PATH`/`LIB`/`INCLUDE` for its link step via `--action_env` without
breaking hermeticity.

Note: because `dotnet.env` wins the merge, `--action_env=ProgramFiles(x86)=...` can **not** override the
isolated value — which is why AOT projects bypass vswhere entirely (next section) rather than trying to
restore the real `ProgramFiles(x86)`.

## What an AOT project must add to its csproj (the divergence from plain dotnet)

| | Plain `dotnet publish` | Under rules_msbuild |
|---|---|---|
| MSVC discovery | `findvcvarsall.bat` -> `vswhere` via real `ProgramFiles(x86)` | isolated; vswhere not findable |
| Linker/libs source | populated by vcvars | taken from environment (`PATH` + `LIB`) |
| Required csproj switch | none | `<IlcUseEnvironmentalTools>true</IlcUseEnvironmentalTools>` |

So an AOT `<PublishAot>` project adds, in its Release `PropertyGroup`:

```xml
<IlcUseEnvironmentalTools>true</IlcUseEnvironmentalTools>
```

This makes ILC **skip** the `findvcvarsall.bat` probe (which also skips the "Platform linker not found"
`<Error>`, since that error only fires when the probe runs and fails) and take `link.exe` from `PATH`
and import libraries from the `LIB` environment variable — both supplied by the consuming repo via
`--action_env`, e.g.:

```
common:windows --action_env=PATH=...;C:\<VS>\VC\Tools\MSVC\<ver>\bin\HostX64\x64
common:windows --action_env=LIB="C:\<VS>\VC\Tools\MSVC\<ver>\lib\x64;C:\Program Files (x86)\Windows Kits\10\lib\<sdk>\ucrt\x64;C:\Program Files (x86)\Windows Kits\10\lib\<sdk>\um\x64"
common:windows --action_env=INCLUDE="...ucrt;...um"
common:windows --action_env=PROCESSOR_ARCHITECTURE=AMD64
```

(`PROCESSOR_ARCHITECTURE=AMD64` keeps MSBuild on a 64-bit task host; otherwise the ILLink
`ComputeManagedAssemblies` task can load the wrong-bitness task host and fail with `MSB4062`. Paths
containing spaces are double-quoted because `.bazelrc` tokenizes with Bourne-shell rules.)

The upshot: a csproj that builds fine under a standalone `dotnet publish` can still need
`IlcUseEnvironmentalTools` (and the consuming repo's `--action_env`) to build under Bazel. That extra
configuration is the price of the hermetic, machine-independent build.
