using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Addons
{
    /// <summary>Validates extracted UPM identity and enforces honest Editor/runtime assembly boundaries.</summary>
    internal static class AddonPackageValidator
    {
        internal static bool TryValidate(string directory, AddonManifest manifest, out string error)
        {
            error = null;
            string packageJson = Path.Combine(directory, "package.json");
            if (!File.Exists(packageJson)) { error = "Extracted add-on has no package.json at its root."; return false; }
            try
            {
                var package = JObject.Parse(File.ReadAllText(packageJson));
                string id = (string)package["name"];
                string version = (string)package["version"];
                if (!string.Equals(id, manifest.id, StringComparison.Ordinal) ||
                    !string.Equals(version, manifest.version, StringComparison.Ordinal))
                {
                    error = $"package.json identity '{id}@{version}' does not match signed manifest " +
                            $"'{manifest.id}@{manifest.version}'.";
                    return false;
                }

                string[] assemblyDefinitions = Directory.GetFiles(directory, "*.asmdef", SearchOption.AllDirectories);
                if (assemblyDefinitions.Length == 0)
                {
                    error = "Add-on packages must define explicit assembly boundaries with at least one .asmdef.";
                    return false;
                }
                if (manifest.hasRuntime) return true;

                if (Directory.GetFiles(directory, "*.asmref", SearchOption.AllDirectories).Length > 0)
                {
                    error = "Editor-only add-ons may not use .asmref files that could join a runtime assembly.";
                    return false;
                }
                if (Directory.GetFiles(directory, "*.dll", SearchOption.AllDirectories).Length > 0)
                {
                    error = "Editor-only add-ons may not contain precompiled DLLs; ship Editor-only source asmdefs.";
                    return false;
                }

                foreach (string path in assemblyDefinitions)
                {
                    var asmdef = JObject.Parse(File.ReadAllText(path));
                    var platforms = asmdef["includePlatforms"] as JArray;
                    bool editorOnly = platforms != null && platforms.Count == 1 &&
                        string.Equals((string)platforms[0], "Editor", StringComparison.Ordinal);
                    if (!editorOnly)
                    {
                        error = $"Editor-only add-on assembly '{Path.GetFileName(path)}' must set " +
                                "includePlatforms to exactly [\"Editor\"].";
                        return false;
                    }
                }

                var assemblyRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in assemblyDefinitions)
                    assemblyRoots.Add(Path.GetFullPath(Path.GetDirectoryName(path) ?? directory));
                foreach (string source in Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories))
                {
                    string current = Path.GetFullPath(Path.GetDirectoryName(source) ?? directory);
                    bool covered = false;
                    while (current.StartsWith(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
                    {
                        if (assemblyRoots.Contains(current)) { covered = true; break; }
                        var parent = Directory.GetParent(current);
                        if (parent == null || parent.FullName == current) break;
                        current = parent.FullName;
                    }
                    if (!covered)
                    {
                        error = $"Editor-only source '{Path.GetFileName(source)}' is outside every explicit Editor asmdef boundary.";
                        return false;
                    }
                }
                return true;
            }
            catch (Exception exception)
            {
                error = $"Could not validate extracted package metadata: {exception.Message}";
                return false;
            }
        }
    }
}
