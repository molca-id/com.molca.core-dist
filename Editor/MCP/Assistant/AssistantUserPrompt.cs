using System;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// A decision the model asked the user to make mid-turn via the <c>molca_ask_user</c> tool
    /// (Sprint 25.6). Carries the question and an optional set of short choice labels the UI renders as
    /// buttons. Immutable once constructed.
    /// </summary>
    public sealed class AssistantUserPrompt
    {
        /// <summary>The question to put to the user.</summary>
        public string Question { get; }

        /// <summary>Optional short choice labels rendered as buttons; never null (empty = free-text only).</summary>
        public string[] Options { get; }

        /// <summary>Creates a prompt with a question and optional choice labels.</summary>
        public AssistantUserPrompt(string question, string[] options)
        {
            Question = question ?? string.Empty;
            Options = options ?? Array.Empty<string>();
        }
    }
}
