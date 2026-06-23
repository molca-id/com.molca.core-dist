using System.IO;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.UI.Build
{
    /// <summary>
    /// Writes a materialized tree to a prefab asset (Sprint 59.4). Confines the output to <c>Assets/</c>,
    /// refuses to clobber an existing prefab unless asked, and returns the project-relative path. The
    /// caller owns undo bracketing (snapshot before overwrite) and destroying the transient scene instance.
    /// </summary>
    /// <remarks>
    /// Regen overwrites the whole generated prefab; the documented overrides pattern is to keep hand-tweaks
    /// in a sibling object/prefab variant rather than editing the generated asset in place.
    /// </remarks>
    public static class UguiPrefabWriter
    {
        /// <summary>
        /// Saves <paramref name="root"/> as a prefab at <paramref name="projectRelativePath"/> (a
        /// <c>Assets/…</c> path; <c>.prefab</c> appended if missing). Returns the path, or null with
        /// <paramref name="error"/> set on any problem (no write on error).
        /// </summary>
        public static string Write(GameObject root, string projectRelativePath, bool overwrite, out string error)
        {
            error = null;
            if (root == null) { error = "No root GameObject to write."; return null; }
            if (string.IsNullOrWhiteSpace(projectRelativePath)) { error = "Provide an output path."; return null; }

            projectRelativePath = projectRelativePath.Replace('\\', '/');
            if (!projectRelativePath.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase))
            {
                error = "Output path must be under 'Assets/'.";
                return null;
            }
            if (!projectRelativePath.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
                projectRelativePath += ".prefab";

            if (!overwrite && File.Exists(projectRelativePath))
            {
                error = $"A prefab already exists at '{projectRelativePath}'. Pass overwrite:true to replace it.";
                return null;
            }

            var dir = Path.GetDirectoryName(projectRelativePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            PrefabUtility.SaveAsPrefabAsset(root, projectRelativePath, out bool success);
            if (!success)
            {
                error = $"Unity could not save the prefab at '{projectRelativePath}'.";
                return null;
            }
            return projectRelativePath;
        }
    }
}
