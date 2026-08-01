using System;
using Molca.Logging;
using UnityEngine;

namespace Molca
{
    /// <summary>
    /// Obsolete. Superseded by <see cref="MolcaLogPipeline"/> and <see cref="ILogSink"/>.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Runtime/Runtime/</c>.
    /// <para/>
    /// Kept so existing references still compile, and deliberately inert. The original class replaced
    /// <c>Debug.unityLogger.logHandler</c> from its constructor and, when its severity filter rejected a
    /// message, returned <i>before</i> forwarding to the handler it had displaced — so a setting that read
    /// as log-file verbosity muted the Unity Console, the player log, and every consumer of
    /// <c>Application.logMessageReceived</c>, including crash reporting.
    /// <para/>
    /// Constructing this type no longer installs anything. Capture is owned by
    /// <see cref="MolcaLogPipeline"/>, which forwards to Unity unconditionally and filters per destination;
    /// verbosity is configured on <see cref="LogManager"/>. The two setters below are no-ops, because
    /// honouring them would mean re-creating the global mute they used to cause.
    /// </remarks>
    [Obsolete("Log capture is owned by MolcaLogPipeline and filtering by ILogSink.MinimumLevel. "
              + "Configure verbosity on LogManager; add destinations with MolcaLogPipeline.AddSink. "
              + "This type is inert and is planned for removal in the next major.")]
    public class LogHandler : ILogHandler, IDisposable
    {
        /// <summary>Obsolete. Does not install a log handler.</summary>
        /// <param name="manager">Ignored.</param>
        public LogHandler(LogManager manager)
        {
            _ = manager;
        }

        /// <inheritdoc/>
        /// <remarks>Forwards to Unity. This type is never installed, so it is normally never called.</remarks>
        public void LogFormat(LogType logType, UnityEngine.Object context, string format,
            params object[] args) =>
            Debug.unityLogger.logHandler.LogFormat(logType, context, format, args);

        /// <inheritdoc/>
        public void LogException(Exception exception, UnityEngine.Object context) =>
            Debug.unityLogger.logHandler.LogException(exception, context);

        /// <summary>Obsolete no-op.</summary>
        /// <param name="minimumLevel">Ignored.</param>
        /// <remarks>
        /// A <see cref="LogType"/> cannot express a threshold: its ordinals are
        /// <c>Error=0, Assert=1, Warning=2, Log=3, Exception=4</c>, so there is no value meaning "all" and
        /// the zero value means "errors only". Use <c>LogManager</c>'s <see cref="MolcaLogLevel"/> fields.
        /// </remarks>
        public void SetMinimumLogLevel(LogType minimumLevel) => _ = minimumLevel;

        /// <summary>Obsolete no-op. Set stack-trace capture on <see cref="LogManager"/>.</summary>
        /// <param name="enabled">Ignored.</param>
        public void SetStackTraceEnabled(bool enabled) => _ = enabled;

        /// <inheritdoc/>
        public void Dispose() { }
    }
}
