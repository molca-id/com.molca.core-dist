using System;
using System.IO;
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
        /// How long a cached retrieval is reused for an unchanged query before re-querying (Sprint 53). Bounds
        /// staleness while collapsing the rapid repeats — retries, edit-resends, quick follow-ups — that would
        /// otherwise each spawn a graphify subprocess.
        /// </summary>
        internal const int MinReuseIntervalSeconds = 120;

        // Last retrieval, keyed on (normalized query, graph stamp) so an unchanged question within the reuse
        // window skips the subprocess and a graph rebuild (stamp change) invalidates it. Static: the retriever
        // is stateless otherwise and a session is one editor process.
        private static string _cacheKey;
        private static RetrievedContext _cachedResult;
        private static DateTime _cachedAtUtc;

        /// <summary>Clock seam for deterministic cache-expiry tests; defaults to wall-clock UTC.</summary>
        internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

        /// <summary>Graph-stamp seam (defaults to <c>graph.json</c> mtime ticks) so cache-invalidation is testable.</summary>
        internal static Func<long> GraphStamp = DefaultGraphStamp;

        private static long DefaultGraphStamp()
        {
            try { return File.Exists(GraphifyCli.GraphJsonPath) ? File.GetLastWriteTimeUtc(GraphifyCli.GraphJsonPath).Ticks : 0; }
            catch { return 0; }
        }

        /// <summary>The normalized cache key for a query: trimmed/lowered text plus the current graph stamp.</summary>
        internal static string ComputeCacheKey(string userText)
            => (userText ?? string.Empty).Trim().ToLowerInvariant() + "|" + GraphStamp();

        /// <summary>Returns the cached result when the key matches and the reuse window has not elapsed (Sprint 53).</summary>
        internal static bool TryGetCached(string userText, out RetrievedContext cached)
        {
            cached = null;
            if (_cachedResult == null) return false;
            if (_cacheKey != ComputeCacheKey(userText)) return false;
            if ((UtcNow() - _cachedAtUtc).TotalSeconds > MinReuseIntervalSeconds) return false;
            cached = _cachedResult;
            return true;
        }

        /// <summary>Stores a retrieval result under the current key/time for later reuse (Sprint 53).</summary>
        internal static void StoreCache(string userText, RetrievedContext result)
        {
            _cacheKey = ComputeCacheKey(userText);
            _cachedResult = result;
            _cachedAtUtc = UtcNow();
        }

        /// <summary>Clears the retrieval cache (test isolation / explicit invalidation).</summary>
        internal static void ClearCache()
        {
            _cacheKey = null;
            _cachedResult = null;
        }

        /// <summary>Restores the clock/graph-stamp seams to their defaults and clears the cache (test teardown).</summary>
        internal static void ResetTestSeams()
        {
            UtcNow = () => DateTime.UtcNow;
            GraphStamp = DefaultGraphStamp;
            ClearCache();
        }

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

            // Reuse a recent identical query (Sprint 53) so retries/edit-resends don't each spawn a subprocess;
            // a graph rebuild changes the stamp and misses, re-querying against the fresh graph.
            if (TryGetCached(userText, out var cached)) return cached;

            try
            {
                // Broad BFS context (no --dfs) suits "what relates to / how does X work" grounding; cap the
                // answer with graphify's own token budget so it stays small at the source.
                var cmd = "query " + GraphifyCli.Quote(userText);
                if (tokenBudget > 0) cmd += " --budget " + tokenBudget;

                var result = await GraphifyCli.RunAsync(cmd, cancellationToken, QueryTimeoutMs);
                if (!result.Ok) return RetrievedContext.None; // NotFound / non-zero exit / timeout → silent no-op

                var text = ShapeRetrieval(result.StdOut, tokenBudget);
                var retrieved = string.IsNullOrEmpty(text)
                    ? RetrievedContext.None
                    : new RetrievedContext { Text = text, Query = userText.Trim() };
                // Cache successful queries (including an empty result) so an identical follow-up is free.
                StoreCache(userText, retrieved);
                return retrieved;
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
