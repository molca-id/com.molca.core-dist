using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Flags hard-coded <c>http(s)://</c> endpoint literals in project/SDK runtime
    /// code. Endpoints belong in <c>HttpRequestAsset</c> ScriptableObjects so they can
    /// be configured per environment without a code change.
    /// </summary>
    /// <remarks>
    /// Consumer-facing: Core's own internals are not audited (locked Sprint-36
    /// decision), so files under <c>Packages/com.molca.core/</c> are skipped. A bare
    /// scheme literal (<c>"https://"</c> used for a <c>StartsWith</c>/prefix concat)
    /// has no host after the <c>//</c> and is intentionally not matched. Suppress an
    /// intentional case with a <c>doctor:ignore</c> comment.
    /// </remarks>
    public class HttpHardcodedUrlCheck : IDoctorCheck
    {
        public string Id => "http-hardcoded-url";
        public string Description => "Hard-coded http(s):// endpoint literals in runtime code (use HttpRequestAsset)";

        // A string literal whose scheme is followed by at least one host character —
        // i.e. a real endpoint, not a bare "https://" scheme prefix.
        private static readonly Regex UrlLiteral = new Regex("\"https?://[^\"\\s]+");

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

                    var match = UrlLiteral.Match(line);
                    if (match.Success)
                    {
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Error,
                            $"Hard-coded URL literal `{match.Value}\"` — move the endpoint to an HttpRequestAsset ScriptableObject.",
                            source.Path, i + 1));
                    }
                }
            }
            return issues;
        }
    }
}
