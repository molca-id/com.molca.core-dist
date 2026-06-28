using System.IO;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Molca.Editor.KnowledgeGraph
{
    /// <summary>
    /// Ensures the resolved <c>com.molca.core</c> package is represented in the graphify build input even
    /// when Core is installed as an immutable UPM package (Git/registry/local) rather than embedded in the
    /// project.
    /// </summary>
    /// <remarks>
    /// graphify indexes the project root and honours <c>.gitignore</c>, which excludes
    /// <c>Library/PackageCache</c>. In <c>framework-unity</c> Core lives under
    /// <c>Packages/com.molca.core</c> (inside the root → already swept), but a <b>consumer</b> resolves Core
    /// into the gitignored package cache, so a root-only sweep would silently build a project-only graph
    /// missing all Core docs/source (Sprint 63.8). When Core is external, this mirrors its consumer-facing
    /// reference docs + Runtime/Editor source into <c>graphify-corpus/com.molca.core/</c> (which <i>is</i>
    /// under the root and indexed) so "how does X work in Core" questions resolve against real content.
    /// Editor-only; main thread.
    /// </remarks>
    public static class CorePackageCorpus
    {
        private const string PackageName = "com.molca.core";

        /// <summary>Corpus subfolder the external package is mirrored into.</summary>
        public static string CorpusSubdir => Path.Combine(GraphifyCli.CorpusDir, PackageName);

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
        /// If Core is resolved as an external package, mirrors its reference docs + source into the corpus so
        /// graphify indexes them; if Core is embedded under the project root, does nothing (already swept).
        /// Never throws into the build — failures are logged and the build continues.
        /// </summary>
        /// <returns>A short status line describing what happened, for build progress display.</returns>
        public static string ExportIfExternal()
        {
            var info = PackageInfo.FindForPackageName(PackageName);
            if (info == null || string.IsNullOrEmpty(info.resolvedPath))
                return "Core package not resolved; skipping package corpus export.";

            if (!NeedsCorpusExport(info.source))
                return "Core is embedded under the project root; root sweep covers it.";

            try
            {
                if (Directory.Exists(CorpusSubdir)) Directory.Delete(CorpusSubdir, recursive: true);
                Directory.CreateDirectory(CorpusSubdir);

                int copied = 0;
                // Consumer-facing reference docs (the conceptual "how X works" content).
                copied += MirrorTree(Path.Combine(info.resolvedPath, "Documentation~", "reference"),
                    Path.Combine(CorpusSubdir, "reference"), "*.md");
                // Public API surface + implementation for source-level Q&A.
                copied += MirrorTree(Path.Combine(info.resolvedPath, "Runtime"),
                    Path.Combine(CorpusSubdir, "Runtime"), "*.cs");
                copied += MirrorTree(Path.Combine(info.resolvedPath, "Editor"),
                    Path.Combine(CorpusSubdir, "Editor"), "*.cs");

                return $"Exported external Core package corpus ({copied} files) from {info.source} install.";
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[Molca KG] Core package corpus export failed (continuing): {ex.Message}");
                return "Core package corpus export failed; building from project + facts only.";
            }
        }

        /// <summary>Copies every file matching <paramref name="pattern"/> under <paramref name="src"/> into
        /// <paramref name="dst"/>, preserving relative layout. Returns the number of files copied.</summary>
        private static int MirrorTree(string src, string dst, string pattern)
        {
            if (!Directory.Exists(src)) return 0;
            int count = 0;
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
