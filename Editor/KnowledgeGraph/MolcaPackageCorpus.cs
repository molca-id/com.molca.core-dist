using System.Collections.Generic;
using System.IO;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Molca.Editor.KnowledgeGraph
{
    /// <summary>
    /// Ensures every installed Molca package (<c>com.molca.*</c>) is represented in the graphify build input,
    /// even when the package is resolved as an immutable UPM dependency (Git/registry/local) rather than
    /// embedded in the project.
    /// </summary>
    /// <remarks>
    /// graphify indexes the project root and honours <c>.gitignore</c>, which excludes
    /// <c>Library/PackageCache</c>. In <c>framework-unity</c> the Molca packages live under
    /// <c>Packages/com.molca.*</c> (inside the root → already swept), but a <b>consumer</b> resolves them into
    /// the gitignored package cache, so a root-only sweep would silently build a project-only graph missing
    /// all Core/SDK docs/source (Sprint 63.8). For each installed, non-embedded Molca package this mirrors its
    /// consumer-facing reference docs + Runtime/Editor source into <c>graphify-corpus/&lt;package&gt;/</c>
    /// (which <i>is</i> under the root and indexed). Editor-only; main thread.
    /// </remarks>
    public static class MolcaPackageCorpus
    {
        private const string PackagePrefix = "com.molca.";

        /// <summary>The corpus subfolder a given package is mirrored into.</summary>
        public static string CorpusSubdir(string packageName) => Path.Combine(GraphifyCli.CorpusDir, packageName);

        /// <summary>
        /// True when a package with the given <paramref name="source"/> needs its content mirrored into the
        /// corpus. Only an <see cref="PackageSource.Embedded"/> package lives under the indexed project root
        /// (<c>Packages/</c>); every other source resolves into the gitignored <c>Library/PackageCache</c>
        /// (Git/Registry) or an arbitrary external path (Local), which the root sweep does not reach. Pure
        /// for testability.
        /// </summary>
        /// <param name="source">The resolved package source.</param>
        /// <returns><c>true</c> unless the package is embedded under the project.</returns>
        public static bool NeedsCorpusExport(PackageSource source) => source != PackageSource.Embedded;

        /// <summary>
        /// Mirrors the docs/source of every installed, non-embedded <c>com.molca.*</c> package into the
        /// corpus so graphify indexes them; embedded packages (already under the swept project root) are
        /// skipped. Never throws into the build — per-package failures are logged and skipped.
        /// </summary>
        /// <returns>A short status line describing what was exported, for build progress display.</returns>
        public static string ExportInstalledPackages()
        {
            PackageInfo[] packages;
            try { packages = PackageInfo.GetAllRegisteredPackages(); }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[Molca KG] Could not list installed packages (skipping corpus export): {ex.Message}");
                return "Could not list installed packages; building from project + facts only.";
            }

            var exported = new List<string>();
            var embedded = 0;

            foreach (var pkg in packages)
            {
                if (pkg == null || string.IsNullOrEmpty(pkg.name) || !pkg.name.StartsWith(PackagePrefix)) continue;
                if (!NeedsCorpusExport(pkg.source)) { embedded++; continue; }
                if (string.IsNullOrEmpty(pkg.resolvedPath)) continue;

                try
                {
                    var count = MirrorPackage(pkg);
                    exported.Add($"{pkg.name} ({count} files)");
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[Molca KG] Corpus export failed for '{pkg.name}' (skipped): {ex.Message}");
                }
            }

            if (exported.Count == 0)
                return embedded > 0
                    ? $"Molca packages embedded under the project root ({embedded}); root sweep covers them."
                    : "No installed Molca packages to add to the corpus.";

            return "Exported installed Molca package corpus: " + string.Join(", ", exported) + ".";
        }

        /// <summary>Mirrors one package's reference docs + Runtime/Editor source into its corpus subfolder.</summary>
        private static int MirrorPackage(PackageInfo pkg)
        {
            var dst = CorpusSubdir(pkg.name);
            if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
            Directory.CreateDirectory(dst);

            var copied = 0;
            copied += MirrorTree(Path.Combine(pkg.resolvedPath, "Documentation~", "reference"),
                Path.Combine(dst, "reference"), "*.md");
            copied += MirrorTree(Path.Combine(pkg.resolvedPath, "Runtime"), Path.Combine(dst, "Runtime"), "*.cs");
            copied += MirrorTree(Path.Combine(pkg.resolvedPath, "Editor"), Path.Combine(dst, "Editor"), "*.cs");
            return copied;
        }

        /// <summary>Copies every file matching <paramref name="pattern"/> under <paramref name="src"/> into
        /// <paramref name="dst"/>, preserving relative layout. Returns the number of files copied.</summary>
        private static int MirrorTree(string src, string dst, string pattern)
        {
            if (!Directory.Exists(src)) return 0;
            var count = 0;
            foreach (var file in Directory.GetFiles(src, pattern, SearchOption.AllDirectories))
            {
                var rel = file.Substring(src.Length).TrimStart('\\', '/');
                var target = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, overwrite: true);
                count++;
            }
            return count;
        }
    }
}
