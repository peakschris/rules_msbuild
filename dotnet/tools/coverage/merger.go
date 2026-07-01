// Trivial lcov merger for rules_msbuild test targets under `bazel coverage`.
//
// The .NET launcher (dotnet_launcher.go / unix.template.sh) already produces a
// finished, SF-path-rewritten lcov file. This merger only needs to honor Bazel's
// split-coverage-postprocessing contract, mirroring the aspect_rules_jest merger:
//
//   - With --experimental_split_coverage_postprocessing (Linux CI), Bazel runs
//     this binary as a separate action and expects it to produce
//     COVERAGE_OUTPUT_FILE from the raw data the test left in COVERAGE_DIR. The
//     launcher writes that data to $COVERAGE_DIR/coverage.dat, so we copy it.
//   - Without split postprocessing (Windows-local dev), the launcher writes
//     COVERAGE_OUTPUT_FILE directly and this merger must NOT clobber it, so we
//     no-op. (Providing this trivial merger still matters: it replaces Bazel's
//     default lcov merger, which would re-filter by the instrumentation manifest
//     and drop coverlet's finished lcov.)
package main

import (
	"io"
	"os"
	"path/filepath"
)

func main() {
	if os.Getenv("SPLIT_COVERAGE_POST_PROCESSING") != "1" {
		// Non-split: the launcher already wrote COVERAGE_OUTPUT_FILE. Leave it.
		return
	}

	out := os.Getenv("COVERAGE_OUTPUT_FILE")
	if out == "" {
		return
	}
	src := filepath.Join(os.Getenv("COVERAGE_DIR"), "coverage.dat")

	in, err := os.Open(src)
	if err != nil {
		// No coverage produced; write an empty file so Bazel's combine step
		// doesn't error on a missing output.
		_ = os.WriteFile(out, []byte{}, 0644)
		return
	}
	defer in.Close()

	dst, err := os.Create(out)
	if err != nil {
		return
	}
	defer dst.Close()
	_, _ = io.Copy(dst, in)
}
