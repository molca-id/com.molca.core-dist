using System;
using System.Collections.Generic;
using System.IO;
using Molca.Logging;
using UnityEngine;
using UnityEngine.Serialization;

namespace Molca
{
    /// <summary>
    /// Configures the log pipeline for a project: what each destination records, and where log files go.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Runtime/Runtime/</c>.
    /// <b>Base class:</b> <see cref="RuntimeSubsystem"/>.
    /// <b>Registration:</b> a component on the Runtime Manager prefab.
    /// <para/>
    /// <b>This subsystem no longer captures logs.</b> <see cref="MolcaLogPipeline"/> does, from
    /// subsystem-registration time, because logs raised during bootstrap are the ones worth having and no
    /// component that bootstrap instantiates can observe them. This class owns configuration and the file
    /// sink, and drains what the pipeline buffered before it existed.
    /// <para/>
    /// <b>It cannot suppress a message.</b> Thresholds here narrow what Molca's own destinations record.
    /// The Unity Console, the player log, <c>Application.logMessageReceived</c> and anything built on it
    /// always see every message. The previous implementation applied its threshold inside a replacement
    /// <see cref="ILogHandler"/> and returned before forwarding, so a log-file verbosity setting silently
    /// muted <c>Debug</c> process-wide — and because the field was a <see cref="LogType"/>, whose ordinals
    /// are not severity order, the shipped default of <c>0</c> meant <c>Error</c> and discarded every
    /// warning in the framework.
    /// </remarks>
    public class LogManager : RuntimeSubsystem
    {
        [Header("Verbosity")]
        [SerializeField, Tooltip(
             "Lowest severity recorded by Molca's own destinations in a player build. Never affects the "
             + "Unity Console, the player log, or crash reporting — those always receive everything.")]
        private MolcaLogLevel playerLogLevel = MolcaLogLevel.Info;

        [SerializeField, Tooltip(
             "Lowest severity recorded in the Editor and in PlayMode tests. Verbose by default: an author "
             + "at a keyboard wants their own Debug.LogWarning calls to be visible.")]
        private MolcaLogLevel editorLogLevel = MolcaLogLevel.Verbose;

        [Header("Log files")]
        [SerializeField, FormerlySerializedAs("saveToStreamingAssets"), Tooltip(
             "Write rotating log files under Application.persistentDataPath/Logs. Unavailable on WebGL.")]
        private bool writeLogFiles;

        [SerializeField, Tooltip("How many log files to retain, including the current session's.")]
        private int maxLogFiles = 5;

        [SerializeField, Tooltip("Rotate to a new file once the current one passes this size.")]
        private int maxLogSizeInMB = 10;

        [SerializeField, Tooltip("Write stack traces for errors and exceptions into the log file.")]
        private bool includeStackTraces = true;

        private const float FlushIntervalSeconds = 5f;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private double _lastFlushTime;

        private FileLogSink _fileSink;
        private ActionLogSink _legacyCallbackSink;

        /// <summary>The level Molca's destinations are recording at, for this build and platform.</summary>
        public MolcaLogLevel ActiveLevel => Application.isEditor ? editorLogLevel : playerLogLevel;

        /// <summary>The file sink, or <c>null</c> when file logging is off or unavailable.</summary>
        public FileLogSink FileSink => _fileSink;

        /// <summary>The pipeline's rolling in-memory history, or <c>null</c> before installation.</summary>
        /// <remarks>
        /// What an in-game console or the Hub should read. It is populated from the first frame, so it
        /// contains bootstrap logs that no subscriber could have been registered in time to see.
        /// </remarks>
        public MemoryLogSink History => MolcaLogPipeline.Memory;

        /// <summary>Where log files are written, or <c>null</c> when file logging is off.</summary>
        public string LogDirectory { get; private set; }

        /// <summary>Raised for every entry Molca's destinations admit.</summary>
        /// <remarks>
        /// Convenience over registering an <see cref="ILogSink"/>: same threshold as
        /// <see cref="ActiveLevel"/>, and the handler runs on the logging thread, so it must not block and
        /// must not log. Prefer a sink when you need your own threshold or buffering.
        /// </remarks>
        public event Action<MolcaLogEntry> EntryLogged;

        #region Obsolete callback surface

        /// <summary>Invoked for informational messages.</summary>
        [Obsolete("Subscribe to EntryLogged, or register an ILogSink with MolcaLogPipeline.AddSink. "
                  + "These fields deliver a pre-formatted string, so a subscriber cannot filter by "
                  + "severity or reach the context object. Removal is planned for the next major.")]
        public Action<string> onLogInfo;

        /// <summary>Invoked for warnings.</summary>
        [Obsolete("Subscribe to EntryLogged, or register an ILogSink with MolcaLogPipeline.AddSink. "
                  + "Removal is planned for the next major.")]
        public Action<string> onLogWarning;

        /// <summary>Invoked for assertions, errors and exceptions.</summary>
        [Obsolete("Subscribe to EntryLogged, or register an ILogSink with MolcaLogPipeline.AddSink. "
                  + "Removal is planned for the next major.")]
        public Action<string> onLogError;

        #endregion

        /// <inheritdoc/>
        /// <remarks>
        /// Attaches the destinations. Capture is already running by this point, so the first thing this
        /// does after building the file sink is drain the pipeline's buffer into it — that is how a log
        /// written during bootstrap reaches disk despite the writer not having existed yet.
        /// </remarks>
        public override void Initialize(Action<IRuntimeSubsystem> finishCallback)
        {
            // Defensive: the attribute-driven install has already run in a normal session, and Install is
            // idempotent. Calling it here also covers a test that constructed this component directly.
            MolcaLogPipeline.Install();

            if (MolcaLogPipeline.Memory != null)
                MolcaLogPipeline.Memory.MinimumLevel = MolcaLogLevel.Verbose;

            CreateFileSink();
            CreateCallbackSink();

            _lastFlushTime = _clock.Elapsed.TotalSeconds;
            finishCallback?.Invoke(this);
        }

        private void CreateFileSink()
        {
            if (!writeLogFiles) return;

            LogDirectory = Path.Combine(Application.persistentDataPath, "Logs");
            _fileSink = new FileLogSink(LogDirectory, ActiveLevel, maxLogFiles,
                Math.Max(1, maxLogSizeInMB) * 1024L * 1024L, includeStackTraces: includeStackTraces);

            if (!_fileSink.IsAvailable)
            {
                // Unavailable rather than broken on WebGL, where persistentDataPath is an IndexedDB shim.
                _fileSink = null;
                LogDirectory = null;
                return;
            }

            MolcaLogPipeline.AddSink(_fileSink);

            // Everything captured before this sink existed. Drained rather than copied, so a later flush
            // cannot write the same lines twice.
            var buffered = new List<MolcaLogEntry>();
            MolcaLogPipeline.Memory?.DrainTo(buffered);

            foreach (var entry in buffered)
            {
                if (MolcaLogLevels.Passes(entry.Level, _fileSink.MinimumLevel)) _fileSink.Write(entry);
            }

            _fileSink.Flush();
        }

        private void CreateCallbackSink()
        {
            _legacyCallbackSink = new ActionLogSink("log-manager", OnEntry, ActiveLevel);
            MolcaLogPipeline.AddSink(_legacyCallbackSink);
        }

        /// <summary>Fans one entry out to <see cref="EntryLogged"/> and the obsolete callbacks.</summary>
        /// <remarks>
        /// Each subscriber is invoked inside its own try/catch: a throwing handler must not stop the
        /// others, and must not propagate back into the <c>Debug.Log</c> call site.
        /// </remarks>
        private void OnEntry(MolcaLogEntry entry)
        {
            var handlers = EntryLogged;
            if (handlers != null)
            {
                foreach (Action<MolcaLogEntry> handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(entry);
                    }
                    catch (Exception exception)
                    {
                        MolcaLogPipeline.ReportPipelineFailure(exception);
                    }
                }
            }

            InvokeLegacyCallbacks(entry);
        }

#pragma warning disable CS0618 // Obsolete members, invoked here to keep them working for the window.
        private void InvokeLegacyCallbacks(MolcaLogEntry entry)
        {
            // The legacy surface delivered an already-formatted string, so the same is produced here
            // rather than changing what an existing subscriber receives.
            Action<string> target = entry.UnityLogType switch
            {
                LogType.Log => onLogInfo,
                LogType.Warning => onLogWarning,
                _ => onLogError
            };

            if (target == null) return;

            string message = entry.Format(includeStackTrace: false);
            foreach (Action<string> handler in target.GetInvocationList())
            {
                try
                {
                    handler(message);
                }
                catch (Exception exception)
                {
                    MolcaLogPipeline.ReportPipelineFailure(exception);
                }
            }
        }
#pragma warning restore CS0618

        private void Update()
        {
            if (_fileSink == null) return;

            if (_clock.Elapsed.TotalSeconds - _lastFlushTime < FlushIntervalSeconds) return;
            _lastFlushTime = _clock.Elapsed.TotalSeconds;
            _fileSink.Flush();
        }

        /// <remarks>
        /// The frame loop stops before a process is killed or backgrounded, so these two are where a
        /// buffered tail is most likely to be lost. A periodic <see cref="Update"/> flush alone is not
        /// enough on mobile.
        /// </remarks>
        private void OnApplicationPause(bool paused)
        {
            if (paused) _fileSink?.Flush();
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused) _fileSink?.Flush();
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Detaches this subsystem's own sinks and flushes them. Capture itself is deliberately left
        /// installed: it belongs to <see cref="MolcaLogPipeline"/>, it was installed before this component
        /// existed, and logs raised <i>during</i> shutdown are worth keeping.
        /// </remarks>
        public override void Teardown()
        {
            if (_legacyCallbackSink != null)
            {
                MolcaLogPipeline.RemoveSink(_legacyCallbackSink);
                _legacyCallbackSink = null;
            }

            if (_fileSink != null)
            {
                MolcaLogPipeline.RemoveSink(_fileSink);
                _fileSink.Dispose(); // Flushes.
                _fileSink = null;
            }

            EntryLogged = null;
            base.Teardown();
        }

        private void OnDestroy()
        {
            // Teardown normally runs first, via RuntimeManager. This covers a scene teardown that skipped
            // it — losing the tail of a log file is exactly the kind of thing that only happens on the
            // path nobody tested.
            _fileSink?.Flush();
        }

#if UNITY_EDITOR
        /// <remarks>
        /// Applies a threshold edited in the Inspector to the live sinks, so an author changing verbosity
        /// while playing sees the effect immediately instead of on the next launch.
        /// </remarks>
        private void OnValidate()
        {
            maxLogFiles = Mathf.Max(1, maxLogFiles);
            maxLogSizeInMB = Mathf.Max(1, maxLogSizeInMB);

            if (_fileSink != null) _fileSink.MinimumLevel = ActiveLevel;
            if (_legacyCallbackSink != null) _legacyCallbackSink.MinimumLevel = ActiveLevel;
        }
#endif
    }
}
