using System;
using UnityEngine;

namespace Molca
{
    public class LogHandler : ILogHandler, IDisposable
    {
        private readonly LogManager _logManager;
        private readonly ILogHandler _defaultLogHandler;
        
        // Configurable log level filter
        private LogType _minimumLogLevel = LogType.Log;
        private bool _stackTraceEnabled = true;
        // Re-entrancy guard: prevents infinite loops when TMP or other systems log during
        // log handling. Unity invokes ILogHandler on whatever thread called Debug.Log —
        // network/worker threads included — so a single shared bool lets one thread's
        // guard incorrectly gate another thread's unrelated log call (dropped logs, or a
        // stuck-true guard that silences the instance). [ThreadStatic] gives each calling
        // thread its own flag, matching the actual (per-thread) re-entrancy this guards.
        [ThreadStatic] private static bool _isLogging;

        public LogHandler(LogManager manager)
        {
            _logManager = manager;
            _defaultLogHandler = Debug.unityLogger.logHandler;
            Debug.unityLogger.logHandler = this;
        }

        public void Dispose()
        {
            if (Debug.unityLogger.logHandler == this)
            {
                Debug.unityLogger.logHandler = _defaultLogHandler;
            }
        }

        public void LogException(Exception exception, UnityEngine.Object context)
        {
            if (_isLogging || !_logManager.IsActive)
                return;

            _isLogging = true;
            try
            {
                _defaultLogHandler.LogException(exception, context);

                string contextInfo = context != null ? $" [Context: {context.name}]" : string.Empty;
                string stackTrace = _stackTraceEnabled ? $"\nStackTrace:\n{exception.StackTrace}" : string.Empty;
                string message = $"Exception: {exception.Message}{contextInfo}{stackTrace}";

                _logManager.AddLogMessage(message, LogType.Exception);
                _logManager.onLogError?.Invoke(message);
            }
            finally
            {
                _isLogging = false;
            }
        }

        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            if (_isLogging || !_logManager.IsActive || !ShouldLogMessageType(logType))
                return;

            _isLogging = true;
            try
            {
                _defaultLogHandler.LogFormat(logType, context, format, args);

                string message = string.Format(format, args);
                string contextInfo = context != null ? $" [Context: {context.name}]" : string.Empty;
                string stackTrace = ShouldIncludeStackTrace(logType) ? $"\nStackTrace:\n{Environment.StackTrace}" : string.Empty;
                string fullMessage = $"{message}{contextInfo}{stackTrace}";

                _logManager.AddLogMessage(fullMessage, logType);

                // Trigger appropriate callback
                switch (logType)
                {
                    case LogType.Error:
                        _logManager.onLogError?.Invoke(fullMessage);
                        break;

                    case LogType.Exception:
                        _logManager.onLogError?.Invoke(fullMessage);
                        break;

                    case LogType.Assert:
                        _logManager.onLogError?.Invoke(fullMessage);
                        break;

                    case LogType.Warning:
                        _logManager.onLogWarning?.Invoke(fullMessage);
                        break;

                    case LogType.Log:
                        _logManager.onLogInfo?.Invoke(fullMessage);
                        break;
                }
            }
            finally
            {
                _isLogging = false;
            }
        }

        private bool ShouldLogMessageType(LogType logType)
        {
            // LogType's numeric order is NOT severity order (Error=0, Assert=1,
            // Warning=2, Log=3, Exception=4), so compare explicit severity ranks —
            // a raw enum comparison would drop Error/Assert while keeping Log.
            return GetSeverityRank(logType) >= GetSeverityRank(_minimumLogLevel);
        }

        /// <summary>Maps a <see cref="LogType"/> to an ascending severity rank (Log=0 … Exception=4).</summary>
        private static int GetSeverityRank(LogType logType)
        {
            switch (logType)
            {
                case LogType.Log: return 0;
                case LogType.Warning: return 1;
                case LogType.Assert: return 2;
                case LogType.Error: return 3;
                case LogType.Exception: return 4;
                default: return 0;
            }
        }

        private bool ShouldIncludeStackTrace(LogType logType)
        {
            if (!_stackTraceEnabled) return false;
            
            // Only include stack trace for errors and exceptions by default
            return logType == LogType.Error || 
                   logType == LogType.Exception || 
                   logType == LogType.Assert;
        }

        public void SetMinimumLogLevel(LogType minimumLevel)
        {
            _minimumLogLevel = minimumLevel;
        }

        public void SetStackTraceEnabled(bool enabled)
        {
            _stackTraceEnabled = enabled;
        }
    }
}