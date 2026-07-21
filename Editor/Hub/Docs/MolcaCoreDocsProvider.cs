using System.Collections.Generic;
using System.IO;
using Molca.Editor.UI;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Molca.Editor.Hub.Docs
{
    /// <summary>
    /// Built-in docs provider: contributes the reference guides shipped under
    /// <c>Documentation~/reference/</c> of every installed <c>com.molca.*</c> package (Core and any SDK
    /// fork), read straight from disk and titled/categorized from each file's YAML front-matter.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/Docs/</c>.
    /// Base class: <see cref="MolcaDocsProvider"/> (discovered via <c>TypeCache</c>).
    /// The <c>Documentation~</c> folder ends in <c>~</c> so it is never imported into the AssetDatabase; the
    /// files are enumerated from disk via each package's resolved path. Unlike
    /// <see cref="Molca.Editor.KnowledgeGraph.MolcaPackageCorpus"/>, embedded packages are <b>included</b>
    /// (the Molca packages are embedded under <c>Packages/</c> in this repo). This is the convention-scan
    /// half of the fork extension point: a fork adds docs simply by shipping Markdown files with front-matter.
    /// Main thread only (Package Manager query).
    /// </remarks>
    internal sealed class MolcaCoreDocsProvider : MolcaDocsProvider
    {
        private const string PackagePrefix = "com.molca.";
        private const string ReferenceSubdir = "Documentation~/reference";
        private const string CoreReferenceDir = "Packages/com.molca.core/Documentation~/reference";

        /// <inheritdoc />
        public override IEnumerable<MolcaDocEntry> GetDocs()
        {
            var docs = new List<MolcaDocEntry>();
            var scanned = new HashSet<string>();

            foreach (var (dir, owner, productLabel) in ReferenceDirectories())
            {
                if (!scanned.Add(dir) || !Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly))
                    docs.Add(ReadEntry(file, owner, productLabel));
            }

            return docs;
        }

        /// <summary>
        /// Yields each <c>com.molca.*</c> package's reference directory with its owner id and display name
        /// (the default product label), plus a Core-path fallback.
        /// </summary>
        private static IEnumerable<(string dir, string owner, string productLabel)> ReferenceDirectories()
        {
            PackageInfo[] packages = null;
            try { packages = PackageInfo.GetAllRegisteredPackages(); }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[Molca Hub] Could not list packages for docs (using Core only): {ex.Message}");
            }

            if (packages != null)
            {
                foreach (var pkg in packages)
                {
                    if (pkg == null || string.IsNullOrEmpty(pkg.name) || !pkg.name.StartsWith(PackagePrefix)) continue;
                    if (string.IsNullOrEmpty(pkg.resolvedPath)) continue;
                    yield return (Path.Combine(pkg.resolvedPath, ReferenceSubdir), pkg.name, pkg.displayName);
                }
            }

            // Robustness: guarantee Core's docs even if the package query returned nothing for it. The
            // scanned-set in GetDocs de-dups this against the registered-package hit above. A null label lets
            // the registry derive one from the owner key.
            string coreDir = null;
            try { coreDir = Path.GetFullPath(CoreReferenceDir); }
            catch { /* ignore — package hit above already covers the normal case */ }
            if (!string.IsNullOrEmpty(coreDir))
                yield return (coreDir, "com.molca.core", null);
        }

        /// <summary>Builds a doc entry from a file, reading title/category/order/id/product from its front-matter.</summary>
        private static MolcaDocEntry ReadEntry(string file, string owner, string productLabel)
        {
            string title = null, category = null, id = null, product = null;
            var order = DefaultOrder;

            try
            {
                MolcaMarkdown.StripFrontMatter(File.ReadAllText(file), out var meta);
                meta.TryGetValue("title", out title);
                meta.TryGetValue("category", out category);
                meta.TryGetValue("id", out id);
                meta.TryGetValue("product", out product);
                if (meta.TryGetValue("order", out var orderRaw) && int.TryParse(orderRaw, out var parsed))
                    order = parsed;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[Molca Hub] Could not read front-matter from '{file}' (using fallbacks): {ex.Message}");
            }

            var stem = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(id)) id = stem;
            if (string.IsNullOrWhiteSpace(title)) title = stem.Replace('_', ' ');
            if (string.IsNullOrWhiteSpace(category)) category = "Reference";
            // Explicit `product:` front-matter wins; otherwise fall back to the package display name.
            if (string.IsNullOrWhiteSpace(product)) product = productLabel;

            return new MolcaDocEntry(id, title, category, order, file, owner, product);
        }

        // Docs without an `order` sort after ordered ones but before nothing.
        private const int DefaultOrder = 999;
    }
}
