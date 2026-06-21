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
        // Re-entrancy guard: prevents infinite loops when TMP or other systems log during log handling
        private bool _isLogging;

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
            return logType >= _minimumLogLevel;
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