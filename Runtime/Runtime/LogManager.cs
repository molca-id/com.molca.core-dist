using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;

namespace Molca
{
    public class LogManager : RuntimeSubsystem
    {
        private static LogManager instance;

        [SerializeField]
        private LogType minimumLogLevel;

        [SerializeField]
        private bool saveToStreamingAssets = true;

        [SerializeField]
        private int maxLogFiles = 5;

        [SerializeField]
        private int maxLogSizeInMB = 10;

        public Action<string> onLogInfo;
        public Action<string> onLogWarning;
        public Action<string> onLogError;

        private static ILogger logger = Debug.unityLogger;
        private LogHandler _logHandler;
        private string _logDirectory;
        private string _currentLogPath;
        private readonly object _logLock = new object();
        
        // Messages waiting to be written. Flushed to disk when the buffer reaches
        // FlushThreshold or FlushIntervalSeconds elapses, and cleared after each
        // flush — so OnDestroy writes only the unflushed tail, never a duplicate
        // of the whole session log. Capped at MaxBufferedMessages when file output
        // is disabled so memory cannot grow unbounded.
        internal List<string> _logMessages;
        private const int FlushThreshold = 64;
        private const float FlushIntervalSeconds = 5f;
        private const int MaxBufferedMessages = 1000;
        // Unity routes ILogHandler callbacks through to AddLogMessage/FlushPendingLogs
        // on whatever thread called Debug.Log — including background/network threads —
        // so the flush-interval clock cannot use a main-thread-only Unity API
        // (Time.realtimeSinceStartup throws off the main thread). A Stopwatch is a
        // plain BCL type, safe from any thread, and Update() reads the same clock so
        // the two call paths compare consistent values.
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private double _lastFlushTime;
        private int _currentLogSize;

        public override void Initialize(Action<IRuntimeSubsystem> finishCallback)
        {
            instance = this;
            InitializeLogDirectory();

            _logHandler = new LogHandler(this);
            _logHandler.SetMinimumLogLevel(minimumLogLevel);
            _logMessages = new List<string>();
            _lastFlushTime = _clock.Elapsed.TotalSeconds;

            finishCallback?.Invoke(this);
        }

        private void Update()
        {
            if (!saveToStreamingAssets || _logMessages == null) return;

            if (_clock.Elapsed.TotalSeconds - _lastFlushTime >= FlushIntervalSeconds)
            {
                FlushPendingLogs();
            }
        }

        private void InitializeLogDirectory()
        {
            _logDirectory = Path.Combine(Application.persistentDataPath, "Logs");
            
            if (!Directory.Exists(_logDirectory))
            {
                Directory.CreateDirectory(_logDirectory);
            }

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            _currentLogPath = Path.Combine(_logDirectory, $"runtime-log_{timestamp}.txt");
            
            // Perform log rotation if needed
            PerformLogRotation();
        }

        private void PerformLogRotation()
        {
            var logFiles = Directory.GetFiles(_logDirectory, "runtime-log_*.txt")
                                  .OrderByDescending(f => File.GetLastWriteTime(f))
                                  .ToList();

            // Delete old log files if we exceed maxLogFiles
            while (logFiles.Count >= maxLogFiles)
            {
                string oldestLog = logFiles.Last();
                try
                {
                    File.Delete(oldestLog);
                    logFiles.RemoveAt(logFiles.Count - 1);
                }
                catch (Exception e)
                {
                    Debug.LogError($"Failed to delete old log file: {e.Message}");
                }
            }
        }

        internal void AddLogMessage(string message, LogType logType)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string formattedMessage = $"[{timestamp}] [{logType}] {message}";

            bool flushNow = false;
            lock (_logLock)
            {
                _logMessages.Add(formattedMessage);

                if (saveToStreamingAssets)
                {
                    flushNow = _logMessages.Count >= FlushThreshold;
                }
                else if (_logMessages.Count > MaxBufferedMessages)
                {
                    // File output disabled: drop the oldest entries to bound memory.
                    _logMessages.RemoveRange(0, _logMessages.Count - MaxBufferedMessages);
                }
            }

            if (flushNow)
            {
                FlushPendingLogs();
            }
        }

        /// <summary>
        /// Writes all buffered messages to the current log file and clears the buffer.
        /// </summary>
        private void FlushPendingLogs()
        {
            lock (_logLock)
            {
                _lastFlushTime = _clock.Elapsed.TotalSeconds;
                if (_logMessages.Count == 0) return;

                try
                {
                    if (_currentLogSize >= maxLogSizeInMB * 1024 * 1024)
                    {
                        RotateLogFile();
                    }

                    var sb = new StringBuilder();
                    foreach (string message in _logMessages)
                    {
                        sb.AppendLine(message);
                    }

                    string batch = sb.ToString();
                    File.AppendAllText(_currentLogPath, batch);
                    _currentLogSize += Encoding.UTF8.GetByteCount(batch);
                    _logMessages.Clear();
                }
                catch (Exception e)
                {
                    Debug.LogError($"Failed to write to log file: {e.Message}");
                    // Keep the buffer bounded even when the disk write keeps failing.
                    if (_logMessages.Count > MaxBufferedMessages)
                    {
                        _logMessages.RemoveRange(0, _logMessages.Count - MaxBufferedMessages);
                    }
                }
            }
        }

        private void RotateLogFile()
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            _currentLogPath = Path.Combine(_logDirectory, $"runtime-log_{timestamp}.txt");
            _currentLogSize = 0;
            PerformLogRotation();
        }

        private void OnDestroy()
        {
            _logHandler?.Dispose();

            // Only the unflushed tail is written here — flushed messages were
            // cleared from the buffer, so the session log is never duplicated.
            if (saveToStreamingAssets && _logMessages != null)
            {
                FlushPendingLogs();
            }
        }
    }
}