using System;
using System.Threading;
using UnityEngine;

namespace Molca.Logging
{
    /// <summary>
    /// Observes every <c>Debug.Log</c> call by decorating Unity's <see cref="ILogHandler"/>, and forwards
    /// each one to <see cref="MolcaLogPipeline"/>.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Runtime/Logging/</c>.
    /// <b>Registration:</b> installed by <see cref="MolcaLogPipeline.Install"/>; never constructed directly.
    /// <para/>
    /// <b>It cannot suppress.</b> Unity's handler is invoked first and unconditionally, before any filter,
    /// re-entrancy guard or sink runs. That single ordering rule is the fix for the defect this class was
    /// rebuilt to remove: the previous implementation returned early when its severity filter rejected a
    /// message, and because it had <i>replaced</i> Unity's handler rather than wrapping it, the message
    /// never reached the native logger at all. The Console lost it, the player log lost it, and — since
    /// <c>Application.logMessageReceived</c> is raised by the native path — so did the development-player
    /// bridge and any crash reporter. A verbosity setting for a log file quietly became a global mute on
    /// <c>Debug</c>.
    /// <para/>
    /// <b>Why decorate at all,</b> rather than subscribe to <c>Application.logMessageReceivedThreaded</c>?
    /// Only the <see cref="ILogHandler"/> surface carries the <see cref="UnityEngine.Object"/> context from
    /// <c>Debug.LogWarning(message, this)</c>. That context is what turns "this binding has no target
    /// component" into a message you can click, and author-facing diagnostics are the entire reason this
    /// pipeline exists.
    /// </remarks>
    internal sealed class LogCapture : ILogHandler, IDisposable
    {
        private readonly ILogHandler _inner;
        private readonly int _mainThreadId;

        // Per-thread, not shared: Unity dispatches on the calling thread, so one shared flag would let
        // one thread's guard swallow another thread's unrelated log, and a thread that died mid-handler
        // would leave the flag stuck true and silence the process.
        [ThreadStatic] private static bool _isCapturing;

        /// <summary>The handler this one wraps. Exposed so the pipeline can report its own failures.</summary>
        internal ILogHandler Inner => _inner;

        /// <summary>Wraps the currently installed handler and takes its place.</summary>
        /// <param name="mainThreadId">Managed ID of Unity's main thread.</param>
        internal LogCapture(int mainThreadId)
        {
            _mainThreadId = mainThreadId;
            _inner = Debug.unityLogger.logHandler;
            Debug.unityLogger.logHandler = this;
        }

        /// <inheritdoc/>
        /// <remarks>Restores the wrapped handler, but only if this one is still installed — another
        /// decorator may have been layered on top since.</remarks>
        public void Dispose()
        {
            if (ReferenceEquals(Debug.unityLogger.logHandler, this))
                Debug.unityLogger.logHandler = _inner;
        }

        /// <inheritdoc/>
        public void LogFormat(LogType logType, UnityEngine.Object context, string format,
            params object[] args)
        {
            // Unity first, always. Outside the guard and outside any try, so this class is transparent:
            // whatever the engine would have done with the message, it still does.
            _inner.LogFormat(logType, context, format, args);

            if (_isCapturing) return;
            _isCapturing = true;
            try
            {
                MolcaLogPipeline.Dispatch(Build(logType, context, Compose(format, args), null));
            }
            catch (Exception exception)
            {
                MolcaLogPipeline.ReportPipelineFailure(exception);
            }
            finally
            {
                _isCapturing = false;
            }
        }

        /// <inheritdoc/>
        public void LogException(Exception exception, UnityEngine.Object context)
        {
            _inner.LogException(exception, context);

            if (_isCapturing) return;
            _isCapturing = true;
            try
            {
                // The exception's own trace, not Environment.StackTrace: the latter is the handler's
                // stack with this class's frames on top, and for a rethrown exception it describes the
                // wrong place entirely.
                MolcaLogPipeline.Dispatch(Build(LogType.Exception, context,
                    exception != null ? $"{exception.GetType().Name}: {exception.Message}" : "Exception",
                    exception?.StackTrace));
            }
            catch (Exception failure)
            {
                MolcaLogPipeline.ReportPipelineFailure(failure);
            }
            finally
            {
                _isCapturing = false;
            }
        }

        private MolcaLogEntry Build(LogType logType, UnityEngine.Object context, string message,
            string stackTrace)
        {
            bool isMainThread = Thread.CurrentThread.ManagedThreadId == _mainThreadId;

            // Object.name is a main-thread-only property; reading it from a worker thread throws. The
            // name is therefore resolved here or not at all, and only the resolved string is carried
            // forward — a sink must never be handed a live Object reference it could read off-thread or
            // after the object is destroyed.
            string contextName = null;
            if (isMainThread && context != null)
            {
                try
                {
                    contextName = context.name;
                }
                catch (Exception)
                {
                    // A destroyed or otherwise unreadable context is not worth failing a log over.
                }
            }

            return new MolcaLogEntry(logType, message, stackTrace, contextName,
                DateTime.UtcNow, Thread.CurrentThread.ManagedThreadId, isMainThread);
        }

        /// <summary>Applies a format string, falling back to the raw format when it cannot be applied.</summary>
        /// <remarks>
        /// <c>Debug.Log(object)</c> arrives here as <c>format = "{0}"</c> with one argument, but a caller
        /// using <c>Debug.LogFormat</c> can supply a mismatched pair. Unity's own handler has already run
        /// by this point, so throwing here would lose only <i>our</i> copy of a message the Console
        /// already showed — returning the unformatted string keeps it.
        /// </remarks>
        private static string Compose(string format, object[] args)
        {
            if (string.IsNullOrEmpty(format)) return string.Empty;
            if (args == null || args.Length == 0) return format;

            try
            {
                return string.Format(format, args);
            }
            catch (FormatException)
            {
                return format;
            }
            catch (ArgumentNullException)
            {
                return format;
            }
        }
    }
}
