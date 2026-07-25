using System.Collections.Generic;
using Molca.UI.Tokens;

namespace Molca.Editor.UI.Figma
{
    /// <summary>
    /// Validates a <see cref="UiIntentSpec"/> against the spec grammar and a token catalog: every node has
    /// a known <c>type</c> and <c>layout</c>, and every referenced token id resolves in the catalog. This
    /// is the gate that keeps the spec token-referential — the deterministic pre-pass and the model pass
    /// both run their output through here before it's accepted (Sprint 58).
    /// </summary>
    public static class UiIntentSpecValidator
    {
        private static readonly HashSet<string> _nodeTypes =
            new HashSet<string> { "panel", "group", "text", "button", "list", "image" };

        private static readonly HashSet<string> _layouts =
            new HashSet<string> { "vertical", "horizontal", "none" };

        /// <summary>The permitted node <c>type</c> values.</summary>
        public static IReadOnlyCollection<string> NodeTypes => _nodeTypes;

        /// <summary>The permitted <c>layout</c> values.</summary>
        public static IReadOnlyCollection<string> Layouts => _layouts;

        /// <summary>
        /// Sentinel suffix for a token the mapper could not resolve. It is left in the spec for human
        /// review (never a raw hex/value) and is <b>permitted</b> by validation — flagged, not rejected.
        /// </summary>
        public const string UnmappedSuffix = "/_unmapped";

        /// <summary>
        /// Validates <paramref name="spec"/> against <paramref name="catalog"/>. Returns true when there
        /// are no errors; <paramref name="errors"/> always lists every problem found (never null).
        /// </summary>
        public static bool Validate(UiIntentSpec spec, MolcaUiTokenRegistry catalog, out List<string> errors)
        {
            errors = new List<string>();
            if (spec == null) { errors.Add("Spec is null."); return false; }
            if (catalog == null) { errors.Add("Catalog is null."); return false; }
            if (spec.root == null) { errors.Add("Spec has no root node."); return false; }

            ValidateNode(spec.root, catalog, "root", errors);
            return errors.Count == 0;
        }

        private static void ValidateNode(UiIntentNode node, MolcaUiTokenRegistry catalog, string path, List<string> errors)
        {
            if (node == null) { errors.Add($"{path}: null node."); return; }

            if (string.IsNullOrEmpty(node.type) || !_nodeTypes.Contains(node.type))
                errors.Add($"{path}: unknown node type '{node.type}'.");

            if (!string.IsNullOrEmpty(node.layout) && !_layouts.Contains(node.layout))
                errors.Add($"{path}: unknown layout '{node.layout}'.");

            // The primary token is optional (a plain group needs none), but if present it must resolve.
            CheckToken(node.token, catalog, $"{path}.token", errors);
            CheckToken(node.color, catalog, $"{path}.color", errors);
            CheckToken(node.text, catalog, $"{path}.text", errors);

            if (node.children != null)
                for (int i = 0; i < node.children.Count; i++)
                    ValidateNode(node.children[i], catalog, $"{path}.children[{i}]", errors);
        }

        private static void CheckToken(string tokenId, MolcaUiTokenRegistry catalog, string path, List<string> errors)
        {
            if (string.IsNullOrEmpty(tokenId)) return;                 // optional — absent is fine
            if (tokenId.EndsWith(UnmappedSuffix)) return;              // flagged-for-review placeholder, permitted

            if (!MolcaUiTokenId.IsValid(tokenId))
                errors.Add($"{path}: '{tokenId}' is not a valid token id (category/name).");
            else if (!catalog.TryResolve(tokenId, out _))
                errors.Add($"{path}: token '{tokenId}' is not in catalog '{catalog.name}'.");
        }
    }
}
