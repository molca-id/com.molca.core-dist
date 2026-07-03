using System.Collections.Generic;
using System.IO;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Molca.Editor.KnowledgeGraph
{
    /// <summary>
    /// Locates every installed <c>com.molca.*</c> package whose source lives outside the indexed project
    /// root, so a knowledge-graph build can index it <b>in place</b> (as an extra graphify root) instead of
    /// mirroring a copy into the corpus.
    /// </summary>
    /// <remarks>
    /// graphify's default build indexes the project root and honours <c>.gitignore</c>, which excludes
    /// <c>Library/PackageCache</c>. In <c>framework-unity</c> the Molca packages are embedded under
    /// <c>Packages/com.molca.*</c> (inside the root → already swept), but a <b>consumer</b> resolves them into
    /// the gitignored package cache, so a root-only sweep would silently build a project-only graph missing
    /// all Core/SDK docs/source (Sprint 63.8).
    /// <para>
    /// Rather than copy each package's files into <c>graphify-corpus/</c> (duplicated, goes stale on every
    /// package bump), the build passes each external package's <see cref="PackageInfo.resolvedPath"/> to
    /// graphify as an additional index root. graphify treats an explicitly-named path as its own root and
    /// does <i>not</i> apply the enclosing repo's <c>.gitignore</c> to it, so the package cache source is
    /// indexed with zero duplication (Sprint 63.8 → in-place indexing). Editor-only; main thread (the Package
    /// Manager query is main-thread-only).
    /// </para>
    /// </remarks>
    public static class MolcaPackageCorpus
    {
        private const string PackagePrefix = "com.molca.";

        /// <summary>
        /// True when a package with the given <paramref name="source"/> lives outside the indexed project
        /// root and therefore must be indexed in place. Only an <see cref="PackageSource.Embedded"/> package
        /// lives under <c>Packages/</c> (already swept by the root build); every other source resolves into
        /// the gitignored <c>Library/PackageCache</c> (Git/Registry) or an arbitrary external path (Local),
        /// which the root sweep does not reach. Pure for testability.
        /// </summary>
        /// <param name="source">The resolved package source.</param>
        /// <returns><c>true</c> unless the package is embedded under the project.</returns>
        public static bool NeedsCorpusExport(PackageSource source) => source != PackageSource.Embedded;

        /// <summary>
        /// Resolves the filesystem paths of every installed, non-embedded <c>com.molca.*</c> package, for
        /// indexing in place as additional graphify roots. Embedded packages are omitted — they already live
        /// under the swept project root. Never throws — a Package Manager failure logs a warning and yields an
        /// empty list so the build falls back to a project-only graph. Main thread only.
        /// </summary>
        /// <returns>Absolute paths to each external Molca package's resolved location, in install order.</returns>
        public static List<string> ExternalPackagePaths()
        {
            var paths = new List<string>();

            PackageInfo[] packages;
            try { packages = PackageInfo.GetAllRegisteredPackages(); }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[Molca KG] Could not list installed packages (building from project only): {ex.Message}");
                return paths;
            }

            foreach (var pkg in packages)
            {
                if (pkg == null || string.IsNullOrEmpty(pkg.name) || !pkg.name.StartsWith(PackagePrefix)) continue;
                if (!NeedsCorpusExport(pkg.source)) continue;                       // embedded → already under root
                if (string.IsNullOrEmpty(pkg.resolvedPath) || !Directory.Exists(pkg.resolvedPath)) continue;
                paths.Add(pkg.resolvedPath);
            }

            return paths;
        }
    }
}
