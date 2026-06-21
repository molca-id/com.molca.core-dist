using System;

namespace Molca.Editor.Mcp
{
    /// <summary>
    /// A single progress update emitted by a long-running MCP tool while it executes.
    /// </summary>
    /// <remarks>
    /// Progress is advisory and UI-facing only — it never affects a tool's result. <see cref="Fraction"/>
    /// is <c>null</c> for indeterminate work (e.g. an external upload whose duration is unknown), or a
    /// 0–1 completion ratio for phased work.
    /// </remarks>
    public readonly struct McpProgressReport
    {
        /// <summary>Completion ratio in <c>[0, 1]</c>, or <c>null</c> for indeterminate work.</summary>
        public float? Fraction { get; }

        /// <summary>Human-facing status line (e.g. "Building Addressables content…").</summary>
        public string Message { get; }

        /// <summary>Optional coarse phase tag (e.g. "build", "deploy") for grouping or styling.</summary>
        public string Phase { get; }

        /// <summary>Creates a progress report.</summary>
        /// <param name="message">Human-facing status line.</param>
        /// <param name="fraction">Completion ratio in <c>[0, 1]</c>, or <c>null</c> for indeterminate.</param>
        /// <param name="phase">Optional coarse phase tag.</param>
        public McpProgressReport(string message, float? fraction = null, string phase = null)
        {
            Message = message ?? string.Empty;
            Fraction = fraction.HasValue ? UnityEngine.Mathf.Clamp01(fraction.Value) : (float?)null;
            Phase = phase;
        }
    }

    /// <summary>
    /// Ambient progress channel for MCP tool execution. A tool calls <see cref="Report"/> during its run;
    /// whoever is currently driving the tool (today: the in-editor assistant chat) installs a sink via
    /// <see cref="BeginScope"/> to receive those reports and surface them in its UI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sink is a process-wide ambient set around a single tool invocation, mirroring the assistant's
    /// one-tool-at-a-time execution model (the chat controller runs tool calls sequentially on the Unity
    /// main thread). <see cref="BeginScope"/> saves and restores the previous sink, so nesting is safe.
    /// </para>
    /// <para>
    /// Tools that report progress should call <see cref="Report"/> on the main thread. Reports are
    /// best-effort: when no sink is installed (e.g. the HTTP bridge path, which has no progress channel)
    /// <see cref="Report"/> is a no-op, so tools can call it unconditionally.
    /// </para>
    /// </remarks>
    public static class McpProgress
    {
        private static Action<McpProgressReport> _sink;

        /// <summary>
        /// Emits a progress update to the active sink, if any. No-op when no sink is installed.
        /// </summary>
        /// <param name="message">Human-facing status line.</param>
        /// <param name="fraction">Completion ratio in <c>[0, 1]</c>, or <c>null</c> for indeterminate work.</param>
        /// <param name="phase">Optional coarse phase tag.</param>
        public static void Report(string message, float? fraction = null, string phase = null)
            => _sink?.Invoke(new McpProgressReport(message, fraction, phase));

        /// <summary>
        /// Installs <paramref name="sink"/> as the active progress receiver until the returned scope is
        /// disposed, restoring the previously-installed sink on dispose.
        /// </summary>
        /// <param name="sink">Receives every <see cref="Report"/> made while the scope is active.</param>
        /// <returns>A scope that restores the previous sink when disposed.</returns>
        public static IDisposable BeginScope(Action<McpProgressReport> sink) => new Scope(sink);

        private sealed class Scope : IDisposable
        {
            private readonly Action<McpProgressReport> _previous;
            private bool _disposed;

            public Scope(Action<McpProgressReport> sink)
            {
                _previous = _sink;
                _sink = sink;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _sink = _previous;
            }
        }
    }
}
