using System.Collections.Generic;

namespace Molca.Editor.Networking.Authoring
{
    /// <summary>
    /// Outcome of one authoring operation. Structured rather than exception-based so the Hub, MCP
    /// tools, and migration can all report the same failure the same way.
    /// </summary>
    public sealed class NetworkAuthoringResult
    {
        /// <summary>Whether the operation completed and the catalog was modified.</summary>
        public bool Success { get; }

        /// <summary>Human-readable outcome — the reason on failure, a summary on success.</summary>
        public string Message { get; }

        /// <summary>
        /// The identifier the operation created or settled on. Useful when a requested ID collided and
        /// was made unique.
        /// </summary>
        public string ResultId { get; }

        /// <summary>
        /// Entities whose references were rewritten, for the caller to report. Empty for operations
        /// that touch nothing else.
        /// </summary>
        public IReadOnlyList<string> AffectedReferences { get; }

        private NetworkAuthoringResult(
            bool success,
            string message,
            string resultId,
            IReadOnlyList<string> affectedReferences)
        {
            Success = success;
            Message = message;
            ResultId = resultId ?? string.Empty;
            AffectedReferences = affectedReferences ?? System.Array.Empty<string>();
        }

        /// <summary>Creates a success result.</summary>
        /// <param name="message">Summary of what changed.</param>
        /// <param name="resultId">The identifier created or settled on.</param>
        /// <param name="affectedReferences">Entities whose references were rewritten.</param>
        public static NetworkAuthoringResult Ok(
            string message,
            string resultId = null,
            IReadOnlyList<string> affectedReferences = null) =>
            new NetworkAuthoringResult(true, message, resultId, affectedReferences);

        /// <summary>Creates a failure result. Nothing was modified.</summary>
        /// <param name="message">Why the operation was refused.</param>
        public static NetworkAuthoringResult Fail(string message) =>
            new NetworkAuthoringResult(false, message, null, null);

        /// <summary>Renders the result for logs and MCP responses.</summary>
        public override string ToString() => (Success ? "ok: " : "failed: ") + Message;
    }
}
