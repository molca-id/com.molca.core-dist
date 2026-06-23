using System.Collections.Generic;
using UnityEngine;

namespace Molca.UI.Tokens
{
    /// <summary>
    /// Abstract base for a UI token registry — the "style sheet" Core defines but does <b>not</b> populate.
    /// A concrete catalog (Core's <see cref="MolcaUiTokenCatalog"/>, or an SDK/project subclass) supplies
    /// the actual tokens; this base provides id-based lookup over them. Core ships the engine and contract;
    /// the values live in an SDK/project asset, mirroring the Core-vs-SDK layer model.
    /// </summary>
    /// <remarks>
    /// Editor tooling (the token resolver, the Figma materializer) resolves a token id to a
    /// <see cref="MolcaUiToken"/> through <see cref="TryResolve"/> and then applies it via the existing
    /// <c>ColorID</c>/<see cref="Molca.Localization.LocalizedText"/>/sprite mechanisms — so a token-built
    /// object is indistinguishable from a hand-built one and needs no registry at runtime.
    /// </remarks>
    public abstract class MolcaUiTokenRegistry : ScriptableObject
    {
        /// <summary>Every token in this registry. Never null; may be empty.</summary>
        public abstract IReadOnlyList<MolcaUiToken> AllTokens { get; }

        /// <summary>
        /// Assembly-qualified type name of the <c>GraphicRaycaster</c> to attach to a generated world-space
        /// root — e.g. XR Interaction Toolkit's <c>TrackedDeviceGraphicRaycaster</c>. Null/empty means the
        /// materializer falls back to the built-in <c>GraphicRaycaster</c>. This keeps Core XRI-agnostic: an
        /// SDK/project catalog declares the VR raycaster type; Core never references it.
        /// </summary>
        public virtual string VrRaycasterTypeName => null;

        /// <summary>
        /// Resolves <paramref name="tokenId"/> (in <c>category/name</c> form) to its token.
        /// Returns false for null/empty or an unknown id.
        /// </summary>
        public bool TryResolve(string tokenId, out MolcaUiToken token)
        {
            token = null;
            if (string.IsNullOrEmpty(tokenId)) return false;

            var tokens = AllTokens;
            if (tokens == null) return false;

            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t != null && t.Id == tokenId)
                {
                    token = t;
                    return true;
                }
            }
            return false;
        }

        /// <summary>The ids of every token, for editor pickers and validation.</summary>
        public IEnumerable<string> TokenIds
        {
            get
            {
                var tokens = AllTokens;
                if (tokens == null) yield break;
                foreach (var t in tokens)
                    if (t != null && !string.IsNullOrEmpty(t.Id))
                        yield return t.Id;
            }
        }
    }
}
