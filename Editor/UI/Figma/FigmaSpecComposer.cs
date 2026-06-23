using System;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using UnityEngine;
using Molca.UI.Tokens;
using Molca.Editor.Mcp.Assistant;

namespace Molca.Editor.UI.Figma
{
    /// <summary>
    /// The model pass (Sprint 58.4): given the deterministic draft spec + the catalog's token vocabulary,
    /// asks the model to confirm/override semantic choices (primary CTA, list vs. group, ambiguous color)
    /// and fill in <c>locKey</c>s — then <b>re-validates</b> the result against the catalog. The model only
    /// refines; the deterministic draft is the floor. A null/failing/invalid model response yields the
    /// draft unchanged, and the model can never introduce a token outside the catalog (validation rejects
    /// it) or a raw value (the spec has no field for one).
    /// </summary>
    public static class FigmaSpecComposer
    {
        /// <summary>Output token ceiling for the refine call.</summary>
        public const int MaxTokens = 8000;

        /// <summary>
        /// Refines <paramref name="draft"/> via <paramref name="provider"/> and returns a catalog-valid
        /// spec. Returns the draft's spec unchanged when there is no provider, on cancellation-free failure,
        /// or when the model's output fails to parse or validate. Cancellation propagates.
        /// </summary>
        public static async Awaitable<UiIntentSpec> RefineAsync(FigmaTokenMapper.Draft draft,
            MolcaUiTokenRegistry catalog, ILlmProvider provider, string model, CancellationToken cancellationToken)
        {
            if (draft?.Spec == null) return null;
            if (provider == null || catalog == null) return draft.Spec;

            var request = new LlmRequest
            {
                System = SystemPrompt,
                Model = model,
                MaxTokens = MaxTokens,
            };
            request.Messages.Add(LlmMessage.UserText(BuildUserPrompt(catalog, draft.Spec)));

            LlmResponse response;
            try
            {
                response = await provider.SendAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Figma→UI] Spec refinement failed ({ex.Message}); using the deterministic draft.");
                return draft.Spec;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var refined = TryParseSpec(response?.Text);
            if (refined?.root == null) return draft.Spec;

            // The model must not alter caller-supplied / header values — restore them from the draft.
            refined.worldScale = draft.Spec.worldScale;
            refined.minHitCm = draft.Spec.minHitCm;
            refined.catalogId = draft.Spec.catalogId;
            refined.sourceFrame = draft.Spec.sourceFrame;

            // A model that invented a token id or node type loses to the deterministic draft.
            return UiIntentSpecValidator.Validate(refined, catalog, out _) ? refined : draft.Spec;
        }

        private static UiIntentSpec TryParseSpec(string text)
        {
            var json = ExtractJsonObject(text);
            if (json == null) return null;
            try { return JsonConvert.DeserializeObject<UiIntentSpec>(json); }
            catch { return null; }
        }

        // Tolerate the model wrapping JSON in prose or ```json fences: take the outermost { … }.
        private static string ExtractJsonObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            return (start >= 0 && end > start) ? text.Substring(start, end - start + 1) : null;
        }

        private static string BuildUserPrompt(MolcaUiTokenRegistry catalog, UiIntentSpec draft)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Refine this draft UI Intent Spec for a Unity uGUI screen.");
            sb.AppendLine();
            sb.AppendLine("Allowed token ids (use ONLY these, or 'category/_unmapped' if none fits):");
            foreach (var id in catalog.TokenIds) sb.Append("  ").AppendLine(id);
            sb.AppendLine();
            sb.AppendLine("Draft spec (JSON):");
            sb.AppendLine(JsonConvert.SerializeObject(draft, Formatting.Indented));
            return sb.ToString();
        }

        private const string SystemPrompt =
            "You refine a draft UI Intent Spec that maps a Figma frame to Unity uGUI. Output ONLY the spec " +
            "as JSON (the same shape as the draft), nothing else. Rules: (1) Use ONLY token ids from the " +
            "provided vocabulary; never invent a token and never emit a raw color, size, or pixel value. " +
            "(2) If no token fits a color/text, keep or use 'color/_unmapped' / 'text/_unmapped' for human " +
            "review — do not guess a hex. (3) You MAY correct semantic choices: mark the primary call-to-" +
            "action button's color, choose list vs. group for repeated rows, fix obvious mis-typings. " +
            "(4) Fill 'locKey' for text and button nodes with a concise kebab-case key derived from the " +
            "label's meaning. (5) Preserve the node tree structure and the header fields. Return only JSON.";
    }
}
