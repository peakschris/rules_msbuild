load(":xml_util.bzl", "inline_element")

NUGET_BUILD_CONFIG = "NuGet.Build.Config"

def _encode_source_name(name):
    """Encode a NuGet source name as a valid XML element name (spaces -> _x0020_)."""
    return name.replace(" ", "_x0020_")

def prepare_nuget_config(packages_folder, restore_enabled, package_sources, package_credentials = []):
    sources = []
    for source in package_sources:
        sources.append(inline_element("add", source))

    creds = []
    for cred in package_credentials:
        name = _encode_source_name(cred["name"])
        creds.append(
            "<{name}>\n            <add key=\"Username\" value=\"{user}\" />\n            <add key=\"ClearTextPassword\" value=\"{pwd}\" />\n        </{name}>".format(
                name = name,
                user = cred["username"],
                pwd = cred["password"],
            ),
        )

    return {
        "{packages_folder}": packages_folder,
        "{restore_enabled}": str(restore_enabled),
        "{package_sources}": "\n    ".join(sources),
        "{package_credentials}": "\n        ".join(creds),
    }
