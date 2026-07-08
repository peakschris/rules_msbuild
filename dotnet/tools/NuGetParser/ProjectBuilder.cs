#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace NuGetParser
{
    public class ProjectBuilder
    {
        private readonly string? _sdk;
        private readonly XElement _project;
        private readonly string? _sdkVersion;

        public ProjectBuilder(string sdk)
        {
            _sdk = sdk;
            var parts = sdk.Split("/");
            if (parts.Length > 1)
            {
                _sdk = parts[0];
                _sdkVersion = parts[1];
            }

            _project = new XElement("Project",
                new XElement("Import",
                    new XAttribute("Project", "Restore.props")),
                Import("Sdk.props")
            );
        }

        private XElement Import(string project)
        {
            var el = new XElement("Import",
                new XAttribute("Project", project),
                new XAttribute("Sdk", _sdk));

            if (_sdkVersion != null)
            {
                el.Add(new XAttribute("Version", _sdkVersion));
            }

            return el;
        }


        public void SetTfm(string tfm)
        {
            _project.Add(new XElement("PropertyGroup",
                new XElement("TargetFramework", tfm)));
        }

        // Runtime packs are NuGet package-type DotnetPlatform. They must be requested with
        // <PackageDownload Include=".." Version="[x]"/> (exact, bracketed) -- as a plain
        // <PackageReference> the restore rejects them with NU1213.
        private static bool IsRuntimePack(string packageName)
        {
            return packageName.StartsWith("Microsoft.NETCore.App.Runtime.", System.StringComparison.OrdinalIgnoreCase)
                || packageName.StartsWith("Microsoft.AspNetCore.App.Runtime.", System.StringComparison.OrdinalIgnoreCase)
                || packageName.StartsWith("Microsoft.WindowsDesktop.App.Runtime.", System.StringComparison.OrdinalIgnoreCase)
                || packageName.StartsWith("Microsoft.NETCore.App.Host.", System.StringComparison.OrdinalIgnoreCase);
        }

        public void AddPackages(IEnumerable<(string name, string version)> packages)
        {
            _project.Add(new XElement("ItemGroup",
                packages.Select(p =>
                {
                    var isRuntimePack = IsRuntimePack(p.name);
                    var el = new XElement(isRuntimePack ? "PackageDownload" : "PackageReference",
                        new XAttribute("Include", p.name));
                    if (!string.IsNullOrEmpty(p.version))
                        el.Add(new XAttribute("Version", isRuntimePack ? $"[{p.version}]" : p.version));
                    return el;
                })));
        }

        public void Save(string path)
        {
            _project.Add(Import("Sdk.targets"));

            using var textWriter = File.CreateText(path);
            using var writer = XmlWriter.Create(textWriter, new XmlWriterSettings() {Indent = true});
            _project.Save(writer);
        }

        public void AddReferences(List<string> projects)
        {
            _project.Add(new XElement("ItemGroup",
                projects.Select(p => new XElement("ProjectReference",
                    new XAttribute("Include", p)))));
        }
    }
}