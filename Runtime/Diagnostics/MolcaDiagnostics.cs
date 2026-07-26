using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace Molca
{
    /// <summary>Severity of a bounded, vendor-neutral framework diagnostic breadcrumb.</summary>
    public enum MolcaBreadcrumbLevel
    {
        Debug,
        Info,
        Warning,
        Error,
    }

    /// <summary>Immutable diagnostic breadcrumb safe to forward to an optional customer-owned sink.</summary>
    public sealed class MolcaBreadcrumb
    {
        public string Category { get; }
        public string Message { get; }
        public MolcaBreadcrumbLevel Level { get; }
        public IReadOnlyDictionary<string, string> Data { get; }

        public MolcaBreadcrumb(string category, string message,
            MolcaBreadcrumbLevel level = MolcaBreadcrumbLevel.Info,
            IReadOnlyDictionary<string, string> data = null)
        {
            Category = MolcaDiagnosticBounds.Text(category, 64);
            Message = MolcaDiagnosticBounds.Text(message, 256);
            Level = level;
            Data = MolcaDiagnosticBounds.Properties(data);
        }
    }

    /// <summary>Immutable context for an explicitly captured operational exception.</summary>
    public sealed class MolcaDiagnosticContext
    {
        public string Component { get; }
        public IReadOnlyDictionary<string, string> Properties { get; }

        public MolcaDiagnosticContext(string component, IReadOnlyDictionary<string, string> properties = null)
        {
            Component = MolcaDiagnosticBounds.Text(component, 64);
            Properties = MolcaDiagnosticBounds.Properties(properties);
        }
    }

    /// <summary>
    /// Optional diagnostics destination. Implementations must isolate transport failures and must not mutate
    /// application state. Usage telemetry intentionally remains a separate, sanitized contract.
    /// </summary>
    public interface IMolcaDiagnosticsSink
    {
        string Name { get; }
        bool IsEnabled { get; }
        void AddBreadcrumb(MolcaBreadcrumb breadcrumb);
        void CaptureException(Exception exception, MolcaDiagnosticContext context);
        Awaitable FlushAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Vendor-neutral diagnostics facade. With no registered sink every operation is a safe no-op.
    /// Each sink is isolated so diagnostics can never alter application success or failure behavior.
    /// </summary>
    public static class MolcaDiagnostics
    {
        private static readonly object Gate = new object();
        private static readonly List<IMolcaDiagnosticsSink> Sinks = new List<IMolcaDiagnosticsSink>();

        public static IDisposable Register(IMolcaDiagnosticsSink sink)
        {
            if (sink == null) throw new ArgumentNullException(nameof(sink));
            lock (Gate)
                if (!Sinks.Contains(sink)) Sinks.Add(sink);
            return new Registration(sink);
        }

        public static void Unregister(IMolcaDiagnosticsSink sink)
        {
            if (sink == null) return;
            lock (Gate) Sinks.Remove(sink);
        }

        public static void AddBreadcrumb(MolcaBreadcrumb breadcrumb)
        {
            if (breadcrumb == null) return;
            foreach (IMolcaDiagnosticsSink sink in Snapshot())
            {
                try { if (sink.IsEnabled) sink.AddBreadcrumb(breadcrumb); }
                catch { /* Diagnostics must never break or recursively log through application code. */ }
            }
        }

        public static void CaptureException(Exception exception, MolcaDiagnosticContext context = null)
        {
            if (exception == null) return;
            context ??= new MolcaDiagnosticContext("framework");
            foreach (IMolcaDiagnosticsSink sink in Snapshot())
            {
                try { if (sink.IsEnabled) sink.CaptureException(exception, context); }
                catch { /* Sink failures are intentionally isolated. */ }
            }
        }

        public static async Awaitable FlushAsync(CancellationToken cancellationToken = default)
        {
            foreach (IMolcaDiagnosticsSink sink in Snapshot())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (sink.IsEnabled) await sink.FlushAsync(cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* Shutdown continues even when an optional sink cannot flush. */ }
            }
        }

        private static IMolcaDiagnosticsSink[] Snapshot()
        {
            lock (Gate) return Sinks.ToArray();
        }

        private sealed class Registration : IDisposable
        {
            private IMolcaDiagnosticsSink _sink;
            internal Registration(IMolcaDiagnosticsSink sink) => _sink = sink;
            public void Dispose()
            {
                IMolcaDiagnosticsSink sink = Interlocked.Exchange(ref _sink, null);
                if (sink != null) Unregister(sink);
            }
        }
    }

    internal static class MolcaDiagnosticBounds
    {
        internal static string Text(string value, int maximum)
        {
            string normalized = (value ?? string.Empty).Trim();
            return normalized.Length <= maximum ? normalized : normalized.Substring(0, maximum);
        }

        internal static IReadOnlyDictionary<string, string> Properties(
            IReadOnlyDictionary<string, string> properties)
        {
            var bounded = new Dictionary<string, string>(StringComparer.Ordinal);
            if (properties == null) return bounded;
            foreach (KeyValuePair<string, string> pair in properties.Take(16))
            {
                string key = Text(pair.Key, 64);
                if (key.Length == 0 || bounded.ContainsKey(key)) continue;
                bounded[key] = Text(pair.Value, 256);
            }
            return bounded;
        }
    }
}
