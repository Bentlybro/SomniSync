#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace SomniumSpace.Worlds.bently.networking.Editor
{
    /// <summary>Copies the self-contained scripts into a Somnium world namespace.</summary>
    public static class SomniumWorldInstaller
    {
        private const string SourceNamespace = "SomniumSpace.Worlds.bently.networking";

        public static bool IsValidCreator(string creator) =>
            !string.IsNullOrEmpty(creator) && Regex.IsMatch(creator, @"^[A-Za-z_][A-Za-z0-9_]{0,63}$");

        public static bool Export(string creator, UnityEngine.Object editorScript, out string message)
        {
            creator = creator == null ? "" : creator.Trim();
            if (!IsValidCreator(creator))
            {
                message = "Enter your Somnium creator ID using letters, numbers, or underscores (start with a letter or underscore).";
                return false;
            }

            var scriptPath = AssetDatabase.GetAssetPath(editorScript);
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(scriptPath);
            if (package == null || string.IsNullOrEmpty(package.resolvedPath))
            {
                message = "Import the SomniSync package through Package Manager before preparing a world copy.";
                return false;
            }

            var sourceRoot = package.resolvedPath;
            if (!Directory.Exists(Path.Combine(sourceRoot, "Runtime")) ||
                !Directory.Exists(Path.Combine(sourceRoot, "Editor")) ||
                !File.Exists(Path.Combine(sourceRoot, "LICENSE.md")) ||
                !File.Exists(Path.Combine(sourceRoot, "NOTICE.md")))
            {
                message = "Could not find the SomniSync scripts and license notices in the installed package.";
                return false;
            }

            var targetName = creator + ".somnisync";
            var targetRoot = Path.Combine(Application.dataPath, "#User", targetName);
            var targetNamespace = "SomniumSpace.Worlds." + creator + ".somnisync";
            // Early world copies placed runtime scripts directly in the creator
            // folder under Core/Components/Debug. Keep that layout and its
            // assembly GUID; adding a second Runtime/ tree duplicates every type.
            bool existingFlatLayout = Directory.Exists(Path.Combine(targetRoot, "Core")) &&
                File.Exists(Path.Combine(targetRoot, targetName + ".asmdef"));
            var files = new Dictionary<string, string>();
            files["LICENSE.md"] = File.ReadAllText(Path.Combine(sourceRoot, "LICENSE.md"));
            files["NOTICE.md"] = File.ReadAllText(Path.Combine(sourceRoot, "NOTICE.md"));
            foreach (var section in new[] { "Runtime", "Editor" })
            {
                foreach (var file in Directory.GetFiles(Path.Combine(sourceRoot, section), "*.cs", SearchOption.AllDirectories))
                {
                    var relative = file.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (existingFlatLayout && section == "Runtime")
                        relative = relative.Substring("Runtime".Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var contents = File.ReadAllText(file).Replace(SourceNamespace, targetNamespace);
                    if (section == "Editor")
                        contents = contents.Replace("Tools/SomniSync Package/", "Tools/SomniSync/");
                    files[relative] = contents;
                }
            }

            if (!existingFlatLayout)
            {
                var runtimeAssembly = "SomniSync." + creator + ".Runtime";
                var editorAssembly = "SomniSync." + creator + ".Editor";
                files[Path.Combine("Runtime", runtimeAssembly + ".asmdef")] =
                    AssemblyDefinition(runtimeAssembly, targetNamespace, null, false);
                files[Path.Combine("Editor", editorAssembly + ".asmdef")] =
                    AssemblyDefinition(editorAssembly, targetNamespace + ".Editor", runtimeAssembly, true);
            }

            int changed = 0;
            foreach (var entry in files)
            {
                var destination = Path.Combine(targetRoot, entry.Key);
                if (!File.Exists(destination) || File.ReadAllText(destination) != entry.Value) changed++;
            }
            if (changed == 0)
            {
                message = "Somnium scripts are already up to date in Assets/#User/" + targetName + ".";
                return true;
            }
            if (Directory.Exists(targetRoot) &&
                !EditorUtility.DisplayDialog("Update SomniSync world scripts",
                    "This will write " + changed + " files in Assets/#User/" + targetName +
                    ". Existing .meta files and scene references are kept. Back up any changes you made directly to these scripts before updating.",
                    "Update scripts", "Cancel"))
            {
                message = "Update cancelled.";
                return false;
            }

            foreach (var entry in files)
            {
                var destination = Path.Combine(targetRoot, entry.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                if (File.Exists(destination) && File.ReadAllText(destination) == entry.Value) continue;
                File.WriteAllText(destination, entry.Value, new UTF8Encoding(false));
            }
            AssetDatabase.Refresh();
            message = "Prepared " + files.Count + " scripts/assemblies in Assets/#User/" + targetName +
                ". Once Unity compiles them, use the components from the " + targetNamespace +
                " namespace. Remove the Package Manager copy before uploading this Somnium world.";
            return true;
        }

        private static string AssemblyDefinition(string assembly, string rootNamespace, string reference, bool editorOnly)
        {
            return "{\n" +
                "  \"name\": \"" + assembly + "\",\n" +
                "  \"rootNamespace\": \"" + rootNamespace + "\",\n" +
                "  \"references\": " + (reference == null ? "[]" : "[\"" + reference + "\"]") + ",\n" +
                "  \"includePlatforms\": " + (editorOnly ? "[\"Editor\"]" : "[]") + ",\n" +
                "  \"autoReferenced\": true\n" +
                "}\n";
        }
    }
}
#endif
