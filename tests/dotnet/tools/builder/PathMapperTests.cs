using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using RulesMSBuild.Tools.Builder;
using RulesMSBuild.Tools.Builder.Launcher;
using Xunit;
using Xunit.Sdk;

namespace RulesMSBuild.Tests.Tools
{
    [CLSCompliant(false)]
    [DataDiscoverer("Xunit.Sdk.InlineDataDiscoverer", "xunit.core")]
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class PathDataAttribute : DataAttribute
    {
        private readonly object[] data;
        public PathDataAttribute(params string[] data)
        {
            if (!data[0].Contains(Path.DirectorySeparatorChar))
            {
                Skip = "Mismatched platform";
            }
            this.data = data.Cast<object>().ToArray();
        }
        
        public override IEnumerable<object[]> GetData(MethodInfo testMethod) => (IEnumerable<object[]>) new object[1][]
        {
            this.data
        };
    }
    
    [Collection(BuildFrameworkTestCollection.TestCollectionName)]
    public class PathMapperTests
    {
        private static PathMapper Init(string input)
        {
            PathMapper.ResetInstance();
            var pathMapper = input.Contains('\\')
                ? new PathMapper(@"C:\o", @"C:\o\e\f")
                : new PathMapper("/o", "/o/e/f");
            return pathMapper;
        }

        // In-memory / --sandbox_base sandbox: exec_root lives on a separate root from output_base
        // (e.g. exec_root under /dev/shm while the SDK tools stay under the real output_base).
        private static PathMapper InitDisjoint(string input)
        {
            PathMapper.ResetInstance();
            var pathMapper = input.Contains('\\')
                ? new PathMapper(@"C:\o", @"D:\shm\e\f")
                : new PathMapper("/o", "/shm/e/f");
            return pathMapper;
        }

        [Theory]
        [PathData(@"C:\o\e\f\file.txt", @"$exec_root\f\file.txt")]
        [PathData("/o/e/f/file.txt", @"$exec_root/f/file.txt")]
        public void RoundTrip_Works(string input, string expectedStored)
        {
            var pathMapper = Init(input);

            var stored = pathMapper.ToBazel(input);
            stored.Should().Be(expectedStored);

            var retrieved = pathMapper.FromBazel(stored);
            retrieved.Should().Be(input);
        }
        
        [Theory]
        [PathData(@"C:\o\e\f\file.txt", @"$exec_root\f\file.txt")]
        [PathData(@"/o/e/f/file.txt", @"$exec_root/f/file.txt")]
        [PathData(@"C:\o\file.txt", @"$output_base\file.txt")]
        [PathData(@"/o/file.txt", @"$output_base/file.txt")]
        public void ToBazel_Works(string input, string expected)
        {
            var pathMapper = Init(input);

            var actual = pathMapper.ToBazel(input);

            actual.Should().Be(expected);
        }
        
        [Theory]
        [PathData(@"C:\o\e\f\a\file.txt", @"f/a/file.txt")]
        [PathData(@"/o/e/f/a/file.txt", @"f/a/file.txt")]
        [PathData(@"C:\o\e\f\external\bar\a\file.txt", @"bar/a/file.txt")]
        [PathData(@"/o/e/f/external/bar/a/file.txt", @"bar/a/file.txt")]
        public void ToManifestPath_Works(string input, string expected)
        {
            var pathMapper = Init(input);

            var actual = pathMapper.ToManifestPath(input);

            actual.Should().Be(expected);
        }
        
        [Theory]
        [PathData(@"$exec_root\f\file.txt", @"C:\o\e\f\file.txt")]
        [PathData(@"$exec_root/f/file.txt", @"/o/e/f/file.txt")]
        [PathData(@"$output_base\file.txt", @"C:\o\file.txt")]
        [PathData(@"$output_base/file.txt", @"/o/file.txt")]
        public void FromBazel_Works(string input, string expected)
        {
            var pathMapper = Init(expected);

            var actual = pathMapper.FromBazel(input);

            actual.Should().Be(expected);
        }

        // With an in-memory / --sandbox_base sandbox, exec_root is NOT nested under output_base. Both roots must
        // still map independently, and paths must round-trip.
        [Theory]
        [PathData(@"D:\shm\e\f\file.txt", @"$exec_root\f\file.txt")]
        [PathData(@"/shm/e/f/file.txt", @"$exec_root/f/file.txt")]
        [PathData(@"C:\o\file.txt", @"$output_base\file.txt")]
        [PathData(@"/o/file.txt", @"$output_base/file.txt")]
        public void ToBazel_Works_WhenExecRootDisjointFromOutputBase(string input, string expected)
        {
            var pathMapper = InitDisjoint(input);

            var stored = pathMapper.ToBazel(input);
            stored.Should().Be(expected);

            var retrieved = pathMapper.FromBazel(stored);
            retrieved.Should().Be(input);
        }
    }
}
