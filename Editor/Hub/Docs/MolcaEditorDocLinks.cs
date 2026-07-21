using System;
using System.IO;
using System.Linq;
using Molca.Editor.UI;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Hub.Docs
{
    /// <summary>
    /// Resolves the Molca editor link scheme (<c>molca://</c>) used inside rendered docs, and builds the
    /// <see cref="MolcaMarkdownOptions"/> that wire it into the <see cref="MolcaMarkdown"/> renderer.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/Docs/</c>. One umbrella scheme with sub-actions
    /// (mirrors the assistant's <c>molca-context://asset/&lt;guid&gt;</c> pattern) keeps <see cref="MolcaMarkdown"/>
    /// single-scheme and generic:
    /// <list type="bullet">
    ///   <item><c>molca://asset/&lt;guid-or-assetPath&gt;</c> — selects and pings the asset in the Project window.</item>
    ///   <item><c>molca://doc/&lt;id&gt;</c> — navigates the docs browser to that <see cref="MolcaDocEntry.Id"/>.</item>
    /// </list>
    /// Editor-only; main thread. Reusable by any surface that renders Markdown (e.g. the assistant).
    /// </remarks>
    public static class MolcaEditorDocLinks
    {
        /// <summary>The umbrella action scheme handled here.</summary>
        public const string Scheme = "molca://";

        /// <summary>
        /// Builds render options that route <see cref="Scheme"/> links to asset-ping and doc-navigation.
        /// </summary>
        /// <param name="onNavigateDoc">Invoked with a doc id for a <c>molca://doc/&lt;id&gt;</c> link; may be <c>null</c>.</param>
        /// <param name="onOpenFile">Optional override for plain file links; defaults to <see cref="MolcaMarkdown.OpenFile"/>.</param>
        /// <returns>Options ready to pass to <see cref="MolcaMarkdown.Render(VisualElement,string,MolcaMarkdownOptions)"/>.</returns>
        public static MolcaMarkdownOptions OptionsFor(Action<string> onNavigateDoc, Action<string, int> onOpenFile = null)
        {
            return new MolcaMarkdownOptions
            {
                ActionScheme = Scheme,
                ActionTooltip = "Open Molca link",
                OnAction = uri => Dispatch(uri, onNavigateDoc),
                // A plain Markdown link to a sibling reference doc (e.g. [x](FOO.md)) navigates in-viewer;
                // any other file link uses the default asset/external open. This keeps ordinary Markdown
                // cross-links (which also render on GitHub) working inside the Hub.
                OnOpenFile = (path, line) => OpenFileOrDoc(path, line, onNavigateDoc, onOpenFile)
            };
        }

        /// <summary>
        /// Opens a file link: if it names another reference doc (matched by file name), navigates the browser
        /// to it; otherwise falls back to <paramref name="onOpenFile"/> or <see cref="MolcaMarkdown.OpenFile"/>.
        /// </summary>
        private static void OpenFileOrDoc(string path, int line, Action<string> onNavigateDoc, Action<string, int> onOpenFile)
        {
            if (onNavigateDoc != null && !string.IsNullOrEmpty(path) && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(path);
                var doc = MolcaDocsRegistry.GetDocs().FirstOrDefault(d =>
                    string.Equals(Path.GetFileName(d.AbsolutePath), name, StringComparison.OrdinalIgnoreCase));
                if (doc != null)
                {
                    onNavigateDoc(doc.Id);
                    return;
                }
            }

            (onOpenFile ?? MolcaMarkdown.OpenFile)(path, line);
        }

        /// <summary>Dispatches a <see cref="Scheme"/> URI to its sub-action (asset ping / doc navigation).</summary>
        /// <param name="uri">The full <c>molca://…</c> URI from the clicked link.</param>
        /// <param name="onNavigateDoc">Invoked with the doc id for a <c>doc/</c> action; may be <c>null</c>.</param>
        public static void Dispatch(string uri, Action<string> onNavigateDoc)
        {
            if (string.IsNullOrEmpty(uri) || !uri.StartsWith(Scheme, StringComparison.Ordinal)) return;

            var action = uri.Substring(Scheme.Length);
            var slash = action.IndexOf('/');
            if (slash <= 0) return;

            var kind = action.Substring(0, slash);
            var payload = action.Substring(slash + 1);
            if (string.IsNullOrEmpty(payload)) return;

            if (string.Equals(kind, "asset", StringComparison.OrdinalIgnoreCase))
                PingAsset(payload);
            else if (string.Equals(kind, "doc", StringComparison.OrdinalIgnoreCase))
                onNavigateDoc?.Invoke(payload);
        }

        /// <summary>
        /// Selects and pings a project asset identified by a 32-char GUID or an asset path (e.g.
        /// <c>Assets/Foo.prefab</c>). No-op with a warning if it cannot be resolved.
        /// </summary>
        /// <param name="guidOrPath">An asset GUID or a project-relative asset path.</param>
        public static void PingAsset(string guidOrPath)
        {
            if (string.IsNullOrWhiteSpace(guidOrPath)) return;

            var path = IsGuid(guidOrPath) ? AssetDatabase.GUIDToAssetPath(guidOrPath) : guidOrPath;
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogWarning($"[Molca Hub] Doc link asset not found: {guidOrPath}");
                return;
            }

            var obj = AssetDatabase.LoadMainAssetAtPath(path);
            if (obj == null)
            {
                Debug.LogWarning($"[Molca Hub] Doc link asset not found at path: {path}");
                return;
            }

            Selection.activeObject = obj;
            EditorGUIUtility.PingObject(obj);
        }

        /// <summary>True when <paramref name="value"/> is a 32-character hex Unity asset GUID.</summary>
        private static bool IsGuid(string value)
        {
            if (value == null || value.Length != 32) return false;
            foreach (var c in value)
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }
    }
}
