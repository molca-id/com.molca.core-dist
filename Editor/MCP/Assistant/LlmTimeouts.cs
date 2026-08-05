namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// The two deadlines a model call is judged by (Sprint 94 follow-up). Separating them is what makes long
    /// turns safe: a reasoning model on a large context can legitimately take many minutes to answer, so
    /// "still streaming" — not total elapsed time — is the signal that it is still working.
    /// </summary>
    /// <remarks>
    /// A single total timeout, which is what <c>HttpClient.Timeout</c> offers, cannot express that and will
    /// kill a healthy long answer mid-stream. Prefer <see cref="Default"/> or values from
    /// <see cref="AssistantSettings"/> over constructing ad-hoc numbers at call sites.
    /// </remarks>
    public readonly struct LlmTimeouts
    {
        /// <summary>Fallback used when a caller supplies none.</summary>
        public static LlmTimeouts Default => new LlmTimeouts(180, 90);

        /// <summary>
        /// Seconds allowed for the model to <em>begin</em> responding. With streaming this bounds only the
        /// time to the first chunk; without it, the whole exchange (there being no progress signal to use).
        /// </summary>
        public int FirstResponseSeconds { get; }

        /// <summary>
        /// Streaming only: longest tolerated gap between chunks before the attempt counts as stalled.
        /// <c>0</c> disables stall detection.
        /// </summary>
        public int StallSeconds { get; }

        /// <summary>Creates a timeout pair. Non-positive <paramref name="firstResponseSeconds"/> falls back to the default.</summary>
        /// <param name="firstResponseSeconds">Time-to-first-response budget in seconds.</param>
        /// <param name="stallSeconds">Inter-chunk stall budget in seconds; 0 disables stall detection.</param>
        public LlmTimeouts(int firstResponseSeconds, int stallSeconds)
        {
            FirstResponseSeconds = firstResponseSeconds > 0 ? firstResponseSeconds : 180;
            StallSeconds = stallSeconds < 0 ? 0 : stallSeconds;
        }
    }
}
