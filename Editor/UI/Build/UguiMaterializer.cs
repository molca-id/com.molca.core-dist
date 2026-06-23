using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Molca.UI.Tokens;
using Molca.Editor.UI.Figma;
using Molca.Editor.UI.Tokens;

namespace Molca.Editor.UI.Build
{
    /// <summary>
    /// Builds the GameObject tree for a validated <see cref="UiIntentSpec"/> (Sprint 59.1) — the
    /// deterministic core of the materializer. <c>control/*</c> nodes instantiate the catalog prefab (so a
    /// generated button is the team's real prefab, <c>ColorIDButton</c> and all); <c>panel</c>/<c>group</c>/
    /// <c>image</c>/<c>text</c> nodes are primitives. <b>All appearance comes from the Sprint-57 resolver</b>
    /// — the materializer never sets a raw color/sprite/PPU itself, the one exception being the magenta
    /// <c>TODO_…</c> placeholder for an <c>_unmapped</c> token (a deliberate, visible review marker).
    /// </summary>
    /// <remarks>
    /// Pure tree build: no layout groups, no VR canvas, no prefab write (those are 59.2–59.4). Editor-only;
    /// no model judgement, so the same spec + catalog always build the same tree.
    /// </remarks>
    public sealed class UguiMaterializer
    {
        /// <summary>Counts + notes describing what a build produced, surfaced to the caller/tool.</summary>
        public sealed class BuildReport
        {
            public int NodesBuilt;
            public int PrefabsInstantiated;
            public int PrimitivesBuilt;
            public int UnmappedPlaceholders;
            /// <summary>Human-readable notes: deferred locKeys, unmapped reasons, skipped tokens.</summary>
            public readonly List<string> Notes = new List<string>();
        }

        /// <summary>The GameObject built for a spec node — consumed by the layout and VR passes.</summary>
        public readonly struct NodeBinding
        {
            public readonly GameObject Go;
            public readonly UiIntentNode Node;
            public NodeBinding(GameObject go, UiIntentNode node) { Go = go; Node = node; }
        }

        private MolcaUiTokenRegistry _catalog;
        private BuildReport _report;
        private readonly List<NodeBinding> _bindings = new List<NodeBinding>();

        /// <summary>Node→GameObject bindings from the last <see cref="Build"/>, in build order.</summary>
        public IReadOnlyList<NodeBinding> Bindings => _bindings;

        /// <summary>
        /// Builds <paramref name="spec"/> into a GameObject hierarchy and returns its root. Appearance is
        /// resolved through <paramref name="catalog"/>. Never null for a spec with a root.
        /// </summary>
        public GameObject Build(UiIntentSpec spec, MolcaUiTokenRegistry catalog, out BuildReport report)
        {
            _catalog = catalog;
            _report = new BuildReport();
            _bindings.Clear();
            report = _report;
            if (spec?.root == null || catalog == null) return null;
            return BuildNode(spec.root, parent: null, path: "root");
        }

        private GameObject BuildNode(UiIntentNode node, Transform parent, string path)
        {
            _report.NodesBuilt++;

            // A button instantiates a self-contained control prefab; a list is a container holding one
            // instantiated row template; everything else is a primitive that recurses the spec children.
            bool ownsChildren = node.type == "button" || node.type == "list";
            GameObject go = node.type switch
            {
                "button" => BuildControl(node, path),
                "list" => BuildList(node, path),
                _ => BuildPrimitive(node, path),
            };

            if (go.transform is RectTransform == false)
                go.AddComponent<RectTransform>();
            if (parent != null)
                go.transform.SetParent(parent, false);

            _bindings.Add(new NodeBinding(go, node));

            if (!ownsChildren && node.children != null)
                for (int i = 0; i < node.children.Count; i++)
                    BuildNode(node.children[i], go.transform, $"{path}.children[{i}]");

            return go;
        }

        // A list container: build the container and instantiate one catalog list-item row as its template.
        // The ScrollRect/stacking rig is the layout pass's job; here we just produce the container + a row.
        private GameObject BuildList(UiIntentNode node, string path)
        {
            var container = new GameObject("List", typeof(RectTransform));
            if (!string.IsNullOrEmpty(node.token) && !IsUnmapped(node.token)
                && _catalog.TryResolve(node.token, out var token)
                && token.Category == MolcaUiTokenCategory.Control && token.Prefab != null)
            {
                var row = PrefabUtility.InstantiatePrefab(token.Prefab) as GameObject
                          ?? Object.Instantiate(token.Prefab);
                row.name = "Row (template)";
                row.transform.SetParent(container.transform, false);
                _report.PrefabsInstantiated++;
            }
            else
            {
                _report.Notes.Add($"{path}: list token '{node.token}' has no catalog row prefab; empty list container.");
            }
            return container;
        }

        private GameObject BuildControl(UiIntentNode node, string path)
        {
            if (!string.IsNullOrEmpty(node.token) && !IsUnmapped(node.token)
                && _catalog.TryResolve(node.token, out var token)
                && token.Category == MolcaUiTokenCategory.Control && token.Prefab != null)
            {
                var instance = PrefabUtility.InstantiatePrefab(token.Prefab) as GameObject
                               ?? Object.Instantiate(token.Prefab);
                instance.name = node.type == "list" ? "List" : "Button";
                _report.PrefabsInstantiated++;
                // An optional color override re-tints the instantiated control via the resolver.
                ApplyColorOverride(node, instance, path);
                if (!string.IsNullOrEmpty(node.locKey))
                    _report.Notes.Add($"{path}: locKey '{node.locKey}' not wired (set the control's label/localization).");
                return instance;
            }

            // No usable control prefab in the catalog → a visible placeholder so the gap is obvious.
            _report.Notes.Add($"{path}: control token '{node.token}' has no catalog prefab; placeholder emitted.");
            return Placeholder($"TODO_{node.type}_no_prefab");
        }

        private GameObject BuildPrimitive(UiIntentNode node, string path)
        {
            // _unmapped anywhere on the node → a magenta TODO marker rather than a silently-wrong element.
            if (IsUnmapped(node.token) || IsUnmapped(node.color) || IsUnmapped(node.text))
            {
                _report.UnmappedPlaceholders++;
                _report.Notes.Add($"{path}: unmapped token left for review.");
                return Placeholder("TODO_unmapped");
            }

            var go = new GameObject(Capitalize(node.type), typeof(RectTransform));

            switch (node.type)
            {
                case "panel":
                case "image":
                    go.AddComponent<Image>(); // a graphic for color/surface to target
                    ApplyToken(node.token, go, path);   // surface/* (sprite + PPU)
                    ApplyColorOverride(node, go, path);
                    break;
                case "text":
                    // The resolver adds LocalizedText (RequireComponent pulls in the TMP text) + the style.
                    ApplyToken(node.text, go, path);
                    ApplyColorOverride(node, go, path);
                    if (!string.IsNullOrEmpty(node.locKey))
                        _report.Notes.Add($"{path}: locKey '{node.locKey}' recorded (localization wiring is a follow-up).");
                    break;
                default: // group: a transparent container, no graphic
                    break;
            }

            _report.PrimitivesBuilt++;
            return go;
        }

        private void ApplyToken(string tokenId, GameObject go, string path)
        {
            if (string.IsNullOrEmpty(tokenId) || IsUnmapped(tokenId)) return;
            if (!MolcaUiTokenResolver.TryApply(_catalog, tokenId, go, out var error))
                _report.Notes.Add($"{path}: token '{tokenId}' not applied — {error}");
        }

        private void ApplyColorOverride(UiIntentNode node, GameObject go, string path)
        {
            if (string.IsNullOrEmpty(node.color) || IsUnmapped(node.color)) return;
            if (!MolcaUiTokenResolver.TryApply(_catalog, node.color, go, out var error))
                _report.Notes.Add($"{path}: color '{node.color}' not applied — {error}");
        }

        // The single sanctioned raw-color use: a visible review marker, not styled appearance.
        private GameObject Placeholder(string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var image = go.AddComponent<Image>();
            image.color = Color.magenta;
            return go;
        }

        private static bool IsUnmapped(string tokenId) =>
            !string.IsNullOrEmpty(tokenId) && tokenId.EndsWith(UiIntentSpecValidator.UnmappedSuffix);

        private static string Capitalize(string s) =>
            string.IsNullOrEmpty(s) ? "Node" : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
