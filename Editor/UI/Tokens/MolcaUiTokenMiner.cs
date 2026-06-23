using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Molca.Localization;
using Molca.UI.Tokens;
using ColorIDComponent = Molca.ColorID.ColorID;

namespace Molca.Editor.UI.Tokens
{
    /// <summary>
    /// Seeds a <see cref="MolcaUiTokenCatalog"/> by mining the styling that real UI prefabs already use —
    /// distinct <c>ColorID</c> swatch/step pairs (<c>color/*</c>), <see cref="LocalizedTextStyleInfo"/>
    /// presets (<c>text/*</c>), 9-sliced background sprites with an inferred PPU reference
    /// (<c>surface/*</c>), and the prefabs themselves as reusable controls (<c>control/*</c>). So the token
    /// vocabulary reflects actual usage instead of a guessed taxonomy.
    /// </summary>
    /// <remarks>
    /// Editor-only authoring tool. The <i>engine</i> lives in Core; running it against an SDK/project's UI
    /// folder to produce that layer's catalog is the SDK/project step (Core ships no token values).
    /// </remarks>
    public static class MolcaUiTokenMiner
    {
        /// <summary>The outcome of a mine pass: the harvested tokens and a few counts for reporting.</summary>
        public sealed class MineResult
        {
            public List<MolcaUiToken> Tokens = new List<MolcaUiToken>();
            public int PrefabsScanned;
            public int ColorTokens;
            public int TextTokens;
            public int SurfaceTokens;
            public int ControlTokens;
        }

        /// <summary>
        /// Scans every prefab under <paramref name="folderPath"/> (project-relative, e.g.
        /// <c>Assets/_MolcaSDK/Level/Prefabs/UI</c>) and returns the harvested tokens, de-duplicated by id.
        /// </summary>
        public static MineResult Mine(string folderPath)
        {
            var result = new MineResult();
            if (string.IsNullOrEmpty(folderPath) || !AssetDatabase.IsValidFolder(folderPath))
                return result;

            // First match wins per id, so iterate prefabs in a stable, path-sorted order for determinism.
            var byId = new Dictionary<string, MolcaUiToken>();
            var paths = AssetDatabase.FindAssets("t:Prefab", new[] { folderPath })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct()
                .OrderBy(p => p, System.StringComparer.Ordinal);

            foreach (var path in paths)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                result.PrefabsScanned++;

                // control/<prefab-name> — the prefab itself is a reusable control.
                AddToken(byId, MolcaUiToken.NewControl($"control/{Kebab(prefab.name)}", prefab));

                // color/<swatch>-<step> — every ColorID in the prefab tree.
                foreach (var colorId in prefab.GetComponentsInChildren<ColorIDComponent>(true))
                {
                    if (string.IsNullOrEmpty(colorId.SwatchName) || string.IsNullOrEmpty(colorId.ColorId)) continue;
                    var id = $"color/{Kebab(colorId.SwatchName)}-{Kebab(colorId.ColorId)}";
                    AddToken(byId, MolcaUiToken.NewColor(id, colorId.SwatchName, colorId.ColorId));
                }

                // text/<preset> — LocalizedText style presets (styleInfo is protected; read serialized).
                foreach (var localizedText in prefab.GetComponentsInChildren<LocalizedText>(true))
                {
                    var so = new SerializedObject(localizedText);
                    var styleInfo = so.FindProperty("styleInfo")?.objectReferenceValue as LocalizedTextStyleInfo;
                    if (styleInfo == null) continue;
                    AddToken(byId, MolcaUiToken.NewText($"text/{Kebab(styleInfo.name)}", styleInfo));
                }

                // surface/<sprite> — 9-sliced background images; infer ReferencePixels from the live PPU.
                foreach (var image in prefab.GetComponentsInChildren<Image>(true))
                {
                    if (image.sprite == null || image.type != Image.Type.Sliced) continue;
                    var id = $"surface/{Kebab(image.sprite.name)}";
                    var referencePixels = InferReferencePixels(image);
                    AddToken(byId, MolcaUiToken.NewSurface(id, image.sprite, image.type, referencePixels));
                }
            }

            result.Tokens = byId.Values.OrderBy(t => t.Id, System.StringComparer.Ordinal).ToList();
            result.ColorTokens = result.Tokens.Count(t => t.Category == MolcaUiTokenCategory.Color);
            result.TextTokens = result.Tokens.Count(t => t.Category == MolcaUiTokenCategory.Text);
            result.SurfaceTokens = result.Tokens.Count(t => t.Category == MolcaUiTokenCategory.Surface);
            result.ControlTokens = result.Tokens.Count(t => t.Category == MolcaUiTokenCategory.Control);
            return result;
        }

        /// <summary>
        /// Mines <paramref name="folderPath"/> and writes the result into a <see cref="MolcaUiTokenCatalog"/>
        /// at <paramref name="catalogAssetPath"/>, creating the asset if it does not exist. Returns the catalog.
        /// </summary>
        public static MolcaUiTokenCatalog MineToCatalog(string folderPath, string catalogAssetPath)
        {
            var result = Mine(folderPath);

            var catalog = AssetDatabase.LoadAssetAtPath<MolcaUiTokenCatalog>(catalogAssetPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<MolcaUiTokenCatalog>();
                AssetDatabase.CreateAsset(catalog, catalogAssetPath);
            }
            catalog.EditorSetTokens(result.Tokens);
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            return catalog;
        }

        // PPU rule, inverted: a prefab authored its pixelsPerUnitMultiplier as ReferencePixels / min(w,h),
        // so ReferencePixels = pixelsPerUnitMultiplier * min(w,h). Falls back to the raw multiplier when the
        // rect has no measurable size at author time.
        private static float InferReferencePixels(Image image)
        {
            var rt = image.rectTransform;
            float min = 0f;
            if (rt != null)
            {
                var size = rt.rect.size;
                min = Mathf.Min(Mathf.Abs(size.x), Mathf.Abs(size.y));
            }
            return min > 0f ? image.pixelsPerUnitMultiplier * min : image.pixelsPerUnitMultiplier;
        }

        private static void AddToken(Dictionary<string, MolcaUiToken> byId, MolcaUiToken token)
        {
            if (token != null && !string.IsNullOrEmpty(token.Id) && !byId.ContainsKey(token.Id))
                byId[token.Id] = token;
        }

        /// <summary>Lower-cases and hyphenates a name into a token-safe segment (no slashes, no spaces).</summary>
        private static string Kebab(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "unnamed";
            var sb = new StringBuilder(raw.Length);
            bool lastDash = false;
            foreach (var ch in raw.Trim())
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                    lastDash = false;
                }
                else if (!lastDash && sb.Length > 0)
                {
                    sb.Append('-');
                    lastDash = true;
                }
            }
            return sb.ToString().Trim('-') is var s && s.Length > 0 ? s : "unnamed";
        }
    }
}
