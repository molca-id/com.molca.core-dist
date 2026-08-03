using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.ColorID.Editor
{
    /// <summary>
    /// Finds the project's ColorID authoring assets by type, wherever the project keeps them, and names
    /// where to create one that does not exist yet.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> The installer and the vocabulary bootstrap used to hardcode
    /// <c>Assets/_MolcaSDK/Settings/Global/…</c> and do <c>LoadAssetAtPath(fixed) ?? CreateAsset(fixed)</c>.
    /// That worked only in a project laid out exactly like the development repository. A consumer who
    /// imports the Starter Project Content sample receives those assets under
    /// <c>Assets/Samples/Molca/&lt;version&gt;/…</c>, which the fixed path never looks at — so Core would
    /// quietly create a <i>second</i>, blank settings asset and configure that one instead, leaving the
    /// branded one the sample shipped orphaned and the project looking mysteriously unthemed.</para>
    /// <para><b>A path is not an identity.</b> The type is. Searching for it survives the sample import,
    /// a project's own folder conventions, and the eventual <c>_MolcaSDK</c> → <c>_Molca</c> rename,
    /// none of which a constant can.</para>
    /// <para><b>Several matches are reported, never guessed.</b> These callers write to the asset they
    /// pick, so choosing wrong silently edits content the author did not mean to touch.</para>
    /// <para>Editor-only; main thread.</para>
    /// </remarks>
    internal static class ColorThemeAssetLocator
    {
        /// <summary>Where an asset is created when the project has none.</summary>
        /// <remarks>
        /// Matches <c>MolcaStarter.SettingsFolder</c>, so everything the framework generates lands in one
        /// place. Deliberately not <c>_MolcaSDK</c>: that folder is named after a package being sunset.
        /// </remarks>
        internal const string DefaultSettingsFolder = "Assets/_Molca/Settings/Global";

        /// <summary>Result of a locate: a path, or the reason there isn't one.</summary>
        internal readonly struct Result
        {
            /// <summary>The located asset's path, or <c>null</c>.</summary>
            public string Path { get; }

            /// <summary>How many assets of the type the project holds.</summary>
            public int MatchCount { get; }

            /// <summary>A message for the user when <see cref="Path"/> is null and it is not simply absent.</summary>
            public string Ambiguity { get; }

            internal Result(string path, int matchCount, string ambiguity)
            {
                Path = path;
                MatchCount = matchCount;
                Ambiguity = ambiguity;
            }

            /// <summary>True when the project holds exactly one and it was found.</summary>
            public bool Found => Path != null;

            /// <summary>True when the project holds several and the caller must not choose.</summary>
            public bool IsAmbiguous => Ambiguity != null;
        }

        /// <summary>Locates the project's sole asset of type <typeparamref name="T"/>.</summary>
        /// <typeparam name="T">The ScriptableObject type to find.</typeparam>
        /// <returns>The result; <see cref="Result.Found"/> is false when there are none or several.</returns>
        internal static Result Locate<T>() where T : ScriptableObject
        {
            var matches = AssetDatabase.FindAssets($"t:{typeof(T).Name}")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .OrderBy(p => p, System.StringComparer.Ordinal)
                .ToArray();

            if (matches.Length == 0)
                return new Result(null, 0, null);

            if (matches.Length > 1)
                return new Result(null, matches.Length,
                    $"This project holds {matches.Length} {typeof(T).Name} assets, and this tool writes to "
                    + "the one it picks. Delete or consolidate the extras first:\n  "
                    + string.Join("\n  ", matches));

            return new Result(matches[0], 1, null);
        }

        /// <summary>
        /// The path of the project's sole asset of type <typeparamref name="T"/>, creating the folder for a
        /// default location when the project has none.
        /// </summary>
        /// <typeparam name="T">The ScriptableObject type.</typeparam>
        /// <param name="defaultFileName">File name to use when creating, e.g. <c>"Color Theme Settings.asset"</c>.</param>
        /// <param name="ambiguity">Set when several exist; the caller must abort and show it.</param>
        /// <returns>An existing asset's path, or the path to create one at; <c>null</c> when ambiguous.</returns>
        internal static string ResolveOrDefault<T>(string defaultFileName, out string ambiguity)
            where T : ScriptableObject
        {
            var located = Locate<T>();
            if (located.IsAmbiguous)
            {
                ambiguity = located.Ambiguity;
                return null;
            }

            ambiguity = null;
            if (located.Found) return located.Path;

            var path = $"{DefaultSettingsFolder}/{defaultFileName}";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            return path;
        }
    }
}
