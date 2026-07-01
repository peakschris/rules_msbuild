package main

import (
	"fmt"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"golang.org/x/sys/execabs"
)

var ctx = struct {
	once  sync.Once
	debug bool
}{}

func diag(msg func()) {
	ctx.once.Do(initDiag)
	if ctx.debug {
		msg()
	}
}

func initDiag() {
	ctx.debug = os.Getenv("DOTNET_LAUNCHER_DEBUG") != ""
}

func LaunchDotnet(args []string, info *LaunchInfo) {
	dotnetEnv := info.GetItem("dotnet_env")

	for _, line := range strings.Split(dotnetEnv, ";") {
		key, value, found := strings.Cut(line, "=")
		if !found || key == "" {
			// Skip malformed lines (no '=', empty key, or blank line)
			continue
		}
		// Trim leading/trailing whitespace from key/value for safety
		key = strings.TrimSpace(key)
		value = strings.TrimSpace(value)
		if err := os.Setenv(key, value); err != nil {
			// Log error but continue (or panic if strictness required)
			log.Printf("Failed to set env %s: %v", key, err)
		}
	}


	workspace := info.GetItem("workspace_name")
	pkg := info.GetItem("package")
	_ = os.Setenv("DOTNET_RUNFILES_WORKSPACE", workspace)
	_ = os.Setenv("DOTNET_RUNFILES_PACKAGE", pkg)

	dotnetBinPath := info.GetPathItem("dotnet_bin_path")
	dotnetCmd := info.GetItem("dotnet_cmd")
	dotnetArgs := append([]string{dotnetBinPath, dotnetCmd}, info.GetListItem("dotnet_args")...)
	targetBinPath := info.GetBuiltPath("target_bin_path")
	assemblyArgs := append([]string{targetBinPath}, info.GetListItem("assembly_args")...)
	assemblyArgs = append(assemblyArgs, args[1:]...)

	if dotnetCmd == "test" {
		xmlFile := os.Getenv("XML_OUTPUT_FILE")
		if xmlFile == "" {
			xmlFile = "test.xml"
		}
		loggerArg := fmt.Sprintf("%s;%s=%s",
			info.GetItem("dotnet_logger"),
			info.GetItem("log_path_arg_name"),
			xmlFile,
		)
		assemblyArgs = append(assemblyArgs, "--logger", loggerArg)
		assemblyArgs = append(assemblyArgs, coverageCollectArgs(info)...)
	}

	newArgs := append(dotnetArgs, assemblyArgs...)

	diag(func() { fmt.Printf("==> launching: \"%s\"\n", strings.Join(newArgs, "\" \"")) })
	code := launch(info, newArgs)
	postProcessCoverage(info)
	os.Exit(code)
}

// coverageCollectArgs returns the `dotnet test` args that turn on coverlet's
// XPlat data collector and emit lcov into $COVERAGE_DIR/_coverlet, but only when
// running under `bazel coverage` (COVERAGE_DIR set) and the adapter was staged.
func coverageCollectArgs(info *LaunchInfo) []string {
	covDir := os.Getenv("COVERAGE_DIR")
	adapterDll := info.Data["coverlet_collector_dll"]
	if covDir == "" || adapterDll == "" {
		return nil
	}
	adapterDir := filepath.Dir(info.GetRunfile(adapterDll))
	return []string{
		"--collect", "XPlat Code Coverage;Format=lcov",
		"--TestAdapterPath", adapterDir,
		"--results-directory", filepath.Join(covDir, "_coverlet"),
	}
}

// postProcessCoverage converts coverlet's lcov output into the file Bazel expects,
// rewriting SF: paths to be workspace-relative. No-op outside `bazel coverage`.
func postProcessCoverage(info *LaunchInfo) {
	covDir := os.Getenv("COVERAGE_DIR")
	if covDir == "" {
		return
	}

	dest := os.Getenv("COVERAGE_OUTPUT_FILE")
	if os.Getenv("SPLIT_COVERAGE_POST_PROCESSING") == "1" {
		dest = filepath.Join(covDir, "coverage.dat")
	}
	if dest == "" {
		return
	}

	matches, _ := filepath.Glob(filepath.Join(covDir, "_coverlet", "*", "coverage.info"))
	if len(matches) == 0 {
		_ = os.WriteFile(dest, []byte{}, 0644)
		return
	}

	newest := matches[0]
	var newestT time.Time
	for _, m := range matches {
		if st, err := os.Stat(m); err == nil && st.ModTime().After(newestT) {
			newestT = st.ModTime()
			newest = m
		}
	}

	data, err := os.ReadFile(newest)
	if err != nil {
		_ = os.WriteFile(dest, []byte{}, 0644)
		return
	}
	_ = os.WriteFile(dest, []byte(rewriteCoverageSF(string(data))), 0644)
}

// rewriteCoverageSF rewrites every `SF:` path in an lcov file to be
// workspace-relative. With DeterministicSourcePaths the compiler maps the bazel
// ExecRoot to `/_/`, so stripping up to and including `/_/` yields e.g.
// `src/oci-extract/oci-extract/Foo.cs`.
func rewriteCoverageSF(lcov string) string {
	debug := os.Getenv("DOTNET_LAUNCHER_DEBUG") != "" || os.Getenv("COVERAGE_DEBUG") != ""
	dumped := 0
	lines := strings.Split(lcov, "\n")
	for i, line := range lines {
		if !strings.HasPrefix(line, "SF:") {
			continue
		}
		raw := strings.ReplaceAll(line[3:], "\\", "/")
		if debug && dumped < 5 {
			fmt.Fprintf(os.Stderr, "INFO[dotnet.coverage]: raw SF: %s\n", raw)
			dumped++
		}
		p := raw
		if idx := strings.Index(p, "/_/"); idx >= 0 {
			p = p[idx+3:]
		} else if idx := strings.Index(p, "/bin/"); idx >= 0 {
			// Fallback: strip up to and including the bazel-out bin dir.
			p = p[idx+len("/bin/"):]
		}
		lines[i] = "SF:" + p
	}
	return strings.Join(lines, "\n")
}

func LaunchDotnetPublish(args []string, info *LaunchInfo) {
	assembly := args[0]
	if strings.HasSuffix(assembly, ".exe") {
		assembly = assembly[:len(assembly)-len(".exe")]
	}
	assembly = assembly + ".dll"

	dotnetHome := os.Getenv("DOTNET_CLI_HOME")
	var dotnet string
	if dotnetHome != "" {
		dotnet = filepath.Join(dotnetHome, "dotnet")
	} else {
		dotnetPath, err := exec.LookPath("dotnet")
		dotnet = dotnetPath
		if err != nil {
			log.Panicf("Could not find 'dotnet' on PATH. Set the environment variable DOTNET_CLI_HOME or install a dotnet runtime. https://dotnet.microsoft.com/download")
		}
	}

	newArgs := append([]string{
		dotnet,
		"exec",
		assembly,
	}, args[1:]...)
	os.Exit(launch(info, newArgs))
}

// launch runs the command and, in "wait" mode, returns its exit code instead of
// exiting the process, so callers can run post-processing (e.g. coverage) first.
func launch(info *LaunchInfo, args []string) int {
	launchMode, ok := info.Data["launch_mode"]
	if !ok {
		launchMode = "wait"
	}
	cmd := execabs.Command(args[0], args[1:]...)

	cmd.Env = os.Environ()
	cmd.Stdin = os.Stdin
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr

	if err := cmd.Start(); err != nil {
		panic(fmt.Errorf("failed to launch command: %s\n%v", cmd.String(), err))
	}

	diag(func() { fmt.Printf("Started PID %d\n", cmd.Process.Pid) })
	if launchMode == "wait" {
		// when bazel runs a command, it will only pay attention to the parent process, not the child, so we need to
		// wait on the cmd for bazel to report out on it
		diag(func() { fmt.Printf("waiting...\n") })
		state, err := cmd.Process.Wait()
		if err != nil {
			panic(fmt.Errorf("failed to wait on cmd %s\n%v", cmd.String(), err))
		}
		diag(func() { fmt.Printf("cmd completed: %s\n", state.String()) })
		return state.ExitCode()
	}

	if err := cmd.Process.Release(); err != nil {
		panic(fmt.Errorf("failed to detach from launched command %s\n%v", cmd.String(), err))
	}
	diag(func() { fmt.Printf("released\n") })
	return 0
}
