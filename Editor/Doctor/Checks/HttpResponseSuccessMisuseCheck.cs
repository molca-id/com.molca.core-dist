using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Flags branching on <c>response.isSuccess</c> without consulting
    /// <c>statusCode</c>. A defensive caller that needs to distinguish HTTP error
    /// classes (e.g. 401 vs 500) should branch on the status code, not only the
    /// success flag.
    /// </summary>
    /// <remarks>
    /// Heuristic and consumer-facing (Core internals skipped). Reported as Warning
    /// because text analysis can't prove the surrounding handler ignores the status.
    /// Suppress with a <c>doctor:ignore</c> comment.
    /// </remarks>
    public class HttpResponseSuccessMisuseCheck : IDoctorCheck
    {
        public string Id => "http-response-success-misuse";
        public string Description => "Branching on response.isSuccess without checking statusCode";

        // `.isSuccess` appearing in a conditional position (if/while/return/ternary/&&/||).
        private static readonly Regex SuccessBranch = new Regex(
            @"\b(if|while|return)\b[^;]*\.isSuccess\b|\.isSuccess\b\s*(\?|&&|\|\|)|(&&|\|\|)\s*[^;]*\.isSuccess\b");

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
                    // A status check on the same line means the caller already inspects it.
                    if (line.Contains("statusCode") || line.Contains("StatusCode"))
                        continue;

                    if (SuccessBranch.IsMatch(line))
                    {
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                            "Branching on `response.isSuccess` without checking `statusCode` — inspect the status code when the error class matters.",
                            source.Path, i + 1));
                    }
                }
            }
            return issues;
        }
    }
}
