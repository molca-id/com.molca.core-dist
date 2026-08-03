using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Molca.Editor.Upgrade
{
    /// <summary>
    /// Finds packages the project still depends on that 2.0 retires.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/Upgrade/</c>.
    /// <para/>
    /// Reads <c>Packages/manifest.json</c> as text rather than through the Package Manager client, which
    /// is asynchronous and would make the whole report wait on it. A dependency line is a flat key, so
    /// text is sufficient and cannot fail on a manifest the client would refuse to parse — which is the
    /// state a project in a broken upgrade is quite likely to be in.
    /// <para/>
    /// Never auto-fixable. Removing a dependency is safe only if nothing in the project still uses it,
    /// and that is precisely what <see cref="RetiredApiUsageDetector"/> reports separately — the two
    /// findings together say whether it is safe to drop, which is a judgement for the person who owns
    /// the project.
    /// </remarks>
    public sealed class RetiredPackageDetector : IMolcaUpgradeDetector
    {
        /// <summary>A package 2.0 retires, and what replaces it.</summary>
        private static readonly (string Name, string Replacement)[] Retired =
        {
            ("com.molca.sdk",
                "its code moved into com.molca.core (Runtime/UI/App), and its assets into the "
                + "\"Starter Project Content\" sample — import that from Package Manager ▸ Molca ▸ Samples"),
        };

        /// <inheritdoc/>
        public string System => "Packages";

        /// <inheritdoc/>
        public IEnumerable<MolcaUpgradeFinding> Detect()
        {
            string manifest = Path.Combine(
                Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty,
                "Packages", "manifest.json");

            if (!File.Exists(manifest)) yield break;

            string text;
            try
            {
                text = File.ReadAllText(manifest);
            }
            catch (Exception exception) when (exception is IOException
                                              || exception is UnauthorizedAccessException)
            {
                yield break;
            }

            foreach (var (name, replacement) in Retired)
            {
                if (text.IndexOf($"\"{name}\"", StringComparison.Ordinal) < 0) continue;

                yield return new MolcaUpgradeFinding(
                    $"packages.retired.{name}",
                    $"The project still depends on '{name}', which 2.0 retires",
                    $"Replacement: {replacement}. Remove the dependency from Packages/manifest.json once "
                    + "nothing references it — the script report above lists anything that still does. A "
                    + "pinned git URL keeps resolving after the source is gone, so this will not fail "
                    + "loudly on its own.",
                    MolcaUpgradeSeverity.Warning,
                    new[] { "Packages/manifest.json" });
            }
        }
    }
}
