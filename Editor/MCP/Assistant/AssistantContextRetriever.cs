using System;
using System.Threading;
using Molca.Editor.KnowledgeGraph;
using UnityEngine;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// The result of a proactive retrieval pass (Sprint 47): the grounding text to inject for a turn, or
    /// an empty result when retrieval was skipped, the graph was missing, or the query failed.
    /// </summary>
    public sealed class RetrievedContext
    {
        /// <summary>Whether usable grounding text was retrieved.</summary>
        public bool HasContent => !string.IsNullOrWhiteSpace(Text);

        /// <summary>The shaped grounding text (already truncated to the token budget).</summary>
        public string Text { get; set; }

        /// <summary>The question that produced this context, for surfacing.</summary>
        public string Query { get; set; }

        /// <summary>An empty (skipped / failed / no-graph) result.</summary>
        public static readonly RetrievedContext None = new RetrievedContext();
    }

    /// <summary>
    /// Proactively retrieves project context for an assistant turn by querying the graphify knowledge graph
    /// with the user's message before the first model call (Sprint 47). Reuses the existing
    /// <see cref="GraphifyCli"/> query path (the same engine behind <c>molca_kg_query</c>) rather than
    /// building a separate index. The retrieved text is injected as <b>transient</b> turn context — never
    /// pinned or persisted — so the first answer is grounded without the model having to remember to query.
    /// </summary>
    /// <remarks>
    /// Degrades to <see cref="RetrievedContext.None"/> whenever it cannot help — graph missing, CLI absent,
    /// query failure, timeout, or a trivial message — so it can never fail a turn. Querying shells out to the
    /// graphify CLI (a subprocess), so it honors the turn's <see cref="CancellationToken"/> and a tight
    /// timeout. Not thread-safe expectations: call from the editor async context.
    /// </remarks>
    public static class AssistantContextRetriever
    {
        /// <summary>Header prefixed to the injected block so the model can tell grounding from the user's words.</summary>
        internal const string RetrievedContextHeader = "[Retrieved project context]";

        /// <summary>Messages shorter than this are treated as trivial (e.g. "yes", "continue") and skipped.</summary>
        internal const int MinQueryLength = 12;

        /// <summary>Query timeout — generous enough for a graph query, short enough not to stall the turn.</summary>
        private const int QueryTimeoutMs = 60_000;

        /// <summary>
        /// A cheap pre-filter so a graph subprocess is not spawned on trivial input (short affirmations,
        /// one-word follow-ups). The model still owns final scoping; this only avoids obviously wasteful
        /// queries.
        /// </summary>
        public static bool ShouldRetrieve(string userText)
        {
            if (string.IsNullOrWhiteSpace(userText)) return false;
            var trimmed = userText.Trim();
            if (trimmed.Length < MinQueryLength) return false;
            // A single token is rarely a researchable question ("ok", "thanks", a bare identifier).
            return trimmed.IndexOf(' ') >= 0;
        }

        /// <summary>
        /// Retrieves grounding context for <paramref name="userText"/> within <paramref name="tokenBudget"/>,
        /// or <see cref="RetrievedContext.None"/> when retrieval is skipped or cannot help.
        /// </summary>
        /// <param name="userText">The user's message to ground.</param>
        /// <param name="tokenBudget">Approximate maximum tokens of context to inject.</param>
        /// <param name="cancellationToken">The turn's lifetime token.</param>
        public static async Awaitable<RetrievedContext> RetrieveAsync(
            string userText, int tokenBudget, CancellationToken cancellationToken)
        {
            if (!ShouldRetrieve(userText)) return RetrievedContext.None;
            if (!GraphifyCli.GraphExists) return RetrievedContext.None;

            try
            {
                // Broad BFS context (no --dfs) suits "what relates to / how does X work" grounding; cap the
                // answer with graphify's own token budget so it stays small at the source.
                var cmd = "query " + GraphifyCli.Quote(userText);
                if (tokenBudget > 0) cmd += " --budget " + tokenBudget;

                var result = await GraphifyCli.RunAsync(cmd, cancellationToken, QueryTimeoutMs);
                if (!result.Ok) return RetrievedContext.None; // NotFound / non-zero exit / timeout → silent no-op

                var text = ShapeRetrieval(result.StdOut, tokenBudget);
                return string.IsNullOrEmpty(text)
                    ? RetrievedContext.None
                    : new RetrievedContext { Text = text, Query = userText.Trim() };
            }
            catch (OperationCanceledException)
            {
                throw; // cancellation belongs to the turn handler
            }
            catch (Exception ex)
            {
                // Retrieval is best-effort; a failure must never fail the turn.
                Debug.LogWarning($"[Molca] Assistant context retrieval failed; continuing without it: {ex.Message}");
                return RetrievedContext.None;
            }
        }

        /// <summary>
        /// Trims raw graphify output and caps it to <paramref name="tokenBudget"/> (≈4 chars/token) so the
        /// grounding block can never blow the context it is meant to inform (Sprint 47). Pure and testable.
        /// </summary>
        internal static string ShapeRetrieval(string rawStdout, int tokenBudget)
        {
            if (string.IsNullOrWhiteSpace(rawStdout)) return string.Empty;
            var text = rawStdout.Trim();

            var charCap = Mathf.Max(0, tokenBudget) * 4;
            if (charCap > 0 && text.Length > charCap)
                text = text.Substring(0, charCap).TrimEnd() + "\n…(truncated)";

            return text;
        }
    }
}
