using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Molca.Settings.Integration.Figma
{
    /// <summary>
    /// Writes generated UI Toolkit assets (UXML/USS) and exports Figma image fills as Unity sprites.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/Figma/</c>.
    /// Registration: static editor utility; not an asset.
    /// <para>
    /// All <see cref="AssetDatabase"/> work must run on the Unity main thread — the MCP bridge already marshals
    /// tool execution onto it (the same hazard as the Content Package deploy output). Image fills are rendered
    /// via the Figma images endpoint, downloaded, written under the output folder, and imported as
    /// <see cref="TextureImporterType.Sprite"/>. <see cref="OperationCanceledException"/> is rethrown so callers
    /// can exit quietly on cancel.
    /// </para>
    /// </remarks>
    public static class FigmaAssetImporter
    {
        /// <summary>An exported sprite plus the USS class whose background-image it should populate.</summary>
        public readonly struct ExportedSprite
        {
            internal ExportedSprite(string ussClass, string fileName, string assetPath)
            {
                UssClass = ussClass;
                FileName = fileName;
                AssetPath = assetPath;
            }

            /// <summary>The USS class the sprite belongs to.</summary>
            public string UssClass { get; }
            /// <summary>The sprite's file name (relative to the USS file, for a <c>url(...)</c> reference).</summary>
            public string FileName { get; }
            /// <summary>The sprite's full project-relative asset path.</summary>
            public string AssetPath { get; }
        }

        /// <summary>
        /// Ensures a project-relative folder (e.g. <c>Assets/FigmaGenerated/Login</c>) exists, creating each
        /// missing segment.
        /// </summary>
        /// <param name="projectRelativeFolder">A path under <c>Assets/</c>.</param>
        public static void EnsureFolder(string projectRelativeFolder)
        {
            if (string.IsNullOrWhiteSpace(projectRelativeFolder)) return;

            var segments = projectRelativeFolder.Replace('\\', '/').TrimEnd('/').Split('/');
            if (segments.Length == 0 || segments[0] != "Assets") return;

            string current = "Assets";
            for (int i = 1; i < segments.Length; i++)
            {
                string next = $"{current}/{segments[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, segments[i]);
                current = next;
            }
        }

        /// <summary>
        /// Writes a text asset (UXML or USS) under a folder and imports it.
        /// </summary>
        /// <param name="folder">The project-relative output folder (created if missing).</param>
        /// <param name="fileName">The file name including extension.</param>
        /// <param name="content">The file content.</param>
        /// <returns>The project-relative asset path written.</returns>
        public static string WriteTextAsset(string folder, string fileName, string content)
        {
            EnsureFolder(folder);
            string assetPath = $"{folder}/{fileName}";
            string fullPath = Path.GetFullPath(assetPath);
            File.WriteAllText(fullPath, content, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(assetPath);
            return assetPath;
        }

        /// <summary>
        /// Renders the given image-fill nodes via the Figma images endpoint, downloads them, writes them under
        /// the output folder, and imports them as sprites.
        /// </summary>
        /// <param name="client">The Figma API client (token-bound).</param>
        /// <param name="fileKey">The file key the nodes belong to.</param>
        /// <param name="fills">The image-fill nodes the translator recorded.</param>
        /// <param name="folder">The project-relative output folder.</param>
        /// <param name="framePrefix">A sanitized frame name used to prefix sprite file names.</param>
        /// <param name="cancellationToken">Cancels the export; cancellation is not an error.</param>
        /// <returns>The exported sprites (one per node Figma could render).</returns>
        public static async Awaitable<List<ExportedSprite>> ExportImageFillsAsync(
            FigmaApiClient client, string fileKey,
            IReadOnlyList<FigmaToUiToolkitTranslator.ImageFill> fills,
            string folder, string framePrefix, CancellationToken cancellationToken = default)
        {
            var exported = new List<ExportedSprite>();
            if (client == null || fills == null || fills.Count == 0)
                return exported;

            var nodeIds = new List<string>();
            foreach (var fill in fills)
                if (!string.IsNullOrEmpty(fill.NodeId)) nodeIds.Add(fill.NodeId);
            if (nodeIds.Count == 0) return exported;

            var urls = await client.GetImagesAsync(fileKey, nodeIds, "png", cancellationToken);
            EnsureFolder(folder);

            foreach (var fill in fills)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(fill.NodeId) || !urls.TryGetValue(fill.NodeId, out var url) || string.IsNullOrEmpty(url))
                    continue;

                var bytes = await client.DownloadBytesAsync(url, cancellationToken);
                if (bytes == null || bytes.Length == 0)
                    continue;

                string fileName = $"{framePrefix}_{Sanitize(fill.NodeId)}.png";
                string assetPath = $"{folder}/{fileName}";
                File.WriteAllBytes(Path.GetFullPath(assetPath), bytes);
                AssetDatabase.ImportAsset(assetPath);

                if (AssetImporter.GetAtPath(assetPath) is TextureImporter importer)
                {
                    importer.textureType = TextureImporterType.Sprite;
                    importer.SaveAndReimport();
                }

                exported.Add(new ExportedSprite(fill.UssClass, fileName, assetPath));
            }

            return exported;
        }

        /// <summary>
        /// Appends <c>background-image</c> rules for the exported sprites to a USS body, referencing each
        /// sprite by its file name (relative to the USS file written into the same folder).
        /// </summary>
        /// <param name="uss">The base USS produced by the translator.</param>
        /// <param name="sprites">The sprites exported by <see cref="ExportImageFillsAsync"/>.</param>
        /// <returns>The USS with background-image rules appended.</returns>
        public static string AppendImageRules(string uss, IReadOnlyList<ExportedSprite> sprites)
        {
            if (sprites == null || sprites.Count == 0) return uss;

            var sb = new StringBuilder(uss);
            sb.AppendLine();
            sb.AppendLine("/* Figma image fills */");
            foreach (var sprite in sprites)
            {
                sb.Append('.').Append(sprite.UssClass).AppendLine(" {");
                sb.Append("    background-image: url(\"").Append(sprite.FileName).AppendLine("\");");
                sb.AppendLine("}");
            }
            return sb.ToString();
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }
    }
}
