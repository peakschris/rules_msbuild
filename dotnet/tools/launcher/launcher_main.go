// Package launcher is a launcher utility for windows that takes care of locating the dotnet binary and the user's
// executable and starting both with the right arguments.
package main

import (
	"fmt"
	"os"
	"path"
	"path/filepath"
	"strings"
)

func fileExists(p string) bool {
	_, err := os.Stat(p)
	return err == nil
}

func main() {
	diag(func() { fmt.Printf("launcher args: %s\n", strings.Join(os.Args, ",")) })

	// Get the actual executable path
	// os.Args[0] might just be the basename when run through bash, so use os.Executable() instead
	execPath, err := os.Executable()
	if err != nil {
		// Fallback to os.Args[0] if os.Executable() fails
		execPath, err = filepath.Abs(os.Args[0])
		// check if exexPath exists, if not panic with the original error
		if err != nil || !fileExists(execPath) {
			panic(fmt.Sprintf("failed to get executable path: %v", err))
		}
	}

	launchInfo, err := GetLaunchInfo(execPath)
	if err != nil {
		panic(fmt.Sprintf("failed to get launch info: %s", err))
	}
	binaryType, present := launchInfo.Data["binary_type"]
	if !present {
		panic(fmt.Sprintf("no binary type in launch info: %s", launchInfo))
	}

	switch binaryType {
	case "Dotnet":
		launchInfo.Runfiles = GetRunfiles()
		LaunchDotnet(os.Args, launchInfo)
	case "DotnetPublish":
		// when we're published, our runfiles were made by rules_msbuild, and the directory is guaranteed to be next to
		// the assembly, no monkey business allowed

		binName := path.Base(launchInfo.GetItem("assembly_name"))
		dir, _ := filepath.Split(os.Args[0])
		runfilesDir := filepath.Join(dir, binName) + ".dll.runfiles"
		_ = os.Setenv("RUNFILES_DIR", runfilesDir)
		_ = os.Setenv("RUNFILES_MANIFEST_FILE", filepath.Join(runfilesDir, "MANIFEST"))
		_ = os.Setenv("RUNFILES_MANIFEST_ONLY", "0")
		launchInfo.Runfiles = GetRunfiles()
		LaunchDotnetPublish(os.Args, launchInfo)
	default:
		_, _ = fmt.Fprintf(os.Stderr, "unkown binary_type: %s", binaryType)
	}
}
