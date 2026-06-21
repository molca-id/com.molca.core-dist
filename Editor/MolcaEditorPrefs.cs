using UnityEditor;
using UnityEngine;

namespace Molca.Editor
{
    /// <summary>
    /// Project-scoped wrapper around <see cref="EditorPrefs"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="EditorPrefs"/> keys are machine-global, so identical Molca keys collide across
    /// different projects on one machine (e.g. foldout state from project A leaking into project B).
    /// This wrapper prefixes every key with a stable token derived from <see cref="Application.dataPath"/>,
    /// isolating values per project. Use plain <see cref="EditorPrefs"/> only for values that are
    /// intentionally machine-global.
    /// </remarks>
    public static class MolcaEditorPrefs
    {
        // Computed once per domain; dataPath is constant for the lifetime of the editor process.
        private static readonly string ProjectToken =
            $"Molca[{Application.dataPath.GetHashCode():X8}].";

        private static string Scoped(string key) => ProjectToken + key;

        /// <summary>Reads a project-scoped bool, returning <paramref name="defaultValue"/> if unset.</summary>
        public static bool GetBool(string key, bool defaultValue = false) => EditorPrefs.GetBool(Scoped(key), defaultValue);

        /// <summary>Writes a project-scoped bool.</summary>
        public static void SetBool(string key, bool value) => EditorPrefs.SetBool(Scoped(key), value);

        /// <summary>Reads a project-scoped int, returning <paramref name="defaultValue"/> if unset.</summary>
        public static int GetInt(string key, int defaultValue = 0) => EditorPrefs.GetInt(Scoped(key), defaultValue);

        /// <summary>Writes a project-scoped int.</summary>
        public static void SetInt(string key, int value) => EditorPrefs.SetInt(Scoped(key), value);

        /// <summary>Reads a project-scoped float, returning <paramref name="defaultValue"/> if unset.</summary>
        public static float GetFloat(string key, float defaultValue = 0f) => EditorPrefs.GetFloat(Scoped(key), defaultValue);

        /// <summary>Writes a project-scoped float.</summary>
        public static void SetFloat(string key, float value) => EditorPrefs.SetFloat(Scoped(key), value);

        /// <summary>Reads a project-scoped string, returning <paramref name="defaultValue"/> if unset.</summary>
        public static string GetString(string key, string defaultValue = "") => EditorPrefs.GetString(Scoped(key), defaultValue);

        /// <summary>Writes a project-scoped string.</summary>
        public static void SetString(string key, string value) => EditorPrefs.SetString(Scoped(key), value);

        /// <summary>Returns true if the project-scoped key exists.</summary>
        public static bool HasKey(string key) => EditorPrefs.HasKey(Scoped(key));

        /// <summary>Deletes the project-scoped key if it exists.</summary>
        public static void DeleteKey(string key) => EditorPrefs.DeleteKey(Scoped(key));
    }
}
