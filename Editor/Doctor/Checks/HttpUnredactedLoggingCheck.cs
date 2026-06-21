using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Flags <c>Debug.Log*</c> calls that emit a raw request URL or headers. Credentials
    /// carried in a query string or auth header would land in the player log; route the
    /// value through <c>LogRedaction.RedactUrl</c>/<c>RedactHeaderValue</c> (or log the
    /// context's <c>RedactedUrl</c>/<c>ToRedactedString()</c>) instead.
    /// </summary>
    /// <remarks>
    /// Heuristic and consumer-facing (Core internals skipped). Reported as Warning;
    /// a line already routed through <c>Redact</c> is treated as compliant. Suppress
    /// with a <c>doctor:ignore</c> comment.
    /// </remarks>
    public class HttpUnredactedLoggingCheck : IDoctorCheck
    {
        public string Id => "http-unredacted-logging";
        public string Description => "Debug.Log of a raw request URL/headers (use LogRedaction)";

        private static readonly Regex DebugLog = new Regex(@"\bDebug\.Log\w*\s*\(");
        // Raw request/response surface that may carry secrets.
        private static readonly Regex RawSurface = new Regex(@"\.(FullUrl|headers)\b|\brequest\.url\b");

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            await Awaitable.BackgroundThreadAsync();

            var issues = new List<DoctorIssue>();
            foreach (var source in context.RuntimeSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (NetworkingCheckScope.IsCoreInternal(source))
                    continue;

                for (int i = 0; i < source.Lines.Length; i++)
                {
                    var line = source.Lines[i];
                    if (DoctorContext.IsSuppressed(line))
                        continue;
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///"))
                        continue;
                    // Already routed through redaction — compliant.
                    if (line.Contains("Redact"))
                        continue;

                    if (DebugLog.IsMatch(line) && RawSurface.IsMatch(line))
                    {
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                            "Logging a raw request URL/headers — redact via LogRedaction (or log the context's RedactedUrl/ToRedactedString()) so credentials don't reach the log.",
                            source.Path, i + 1));
                    }
                }
            }
            return issues;
        }
    }
}
