using System;
using System.IO;
using RulesMSBuild.Tools.Bazel;

namespace Tool
{
    class Program
    {
        static void Main(string[] args)
        {
            var runfiles = Runfiles.Create();
            var foo = runfiles.Rlocation("_main/tests/examples/NuGet/Tool/foo.txt");
            Console.WriteLine("runfile contents: " + File.ReadAllText(foo));
        }
    }
}