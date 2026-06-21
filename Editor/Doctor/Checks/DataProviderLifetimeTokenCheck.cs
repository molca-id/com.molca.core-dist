using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Flags a <c>DataProvider</c> subclass that starts a long-running loop (a
    /// <c>while</c>/<c>for</c> loop, coroutine, or background <c>Awaitable</c> loop)
    /// without keying it on <c>LifetimeToken</c>/<c>ShutdownToken</c>. Such a loop
    /// outlives the provider's deactivation and leaks work after teardown.
    /// </summary>
    /// <remarks>
    /// Heuristic and consumer-facing (Core internals skipped). Reported once per file
    /// as Warning. A file that references either lifetime token anywhere is treated as
    /// compliant. Suppress with a <c>doctor:ignore</c> comment on the loop line.
    /// </remarks>
    public class DataProviderLifetimeTokenCheck : IDoctorCheck
    {
        public string Id => "dataprovider-lifetime-token";
        public string Description => "DataProvider loop not keyed on LifetimeToken/ShutdownToken";

        private static readonly Regex ProviderDecl = new Regex(@"\bclass\s+\w+\s*:\s*(?:[\w<>,\s]*,\s*)?\w*DataProvider\b");
        private static readonly Regex LoopStart = new Regex(@"\bwhile\s*\(\s*true\s*\)|\bfor\s*\(\s*;\s*;\s*\)|\bStartCoroutine\s*\(");

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            await Awaitable.BackgroundThreadAsync();

            var issues = new List<DoctorIssue>();
            foreach (var source in context.RuntimeSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (NetworkingCheckScope.IsCoreInternal(source))
                    continue;

                if (!source.Lines.Any(l => ProviderDecl.IsMatch(l)))
                    continue;
                // A provider that threads either lifetime token is presumed compliant.
                if (source.Lines.Any(l => l.Contains("LifetimeToken") || l.Contains("ShutdownToken")))
                    continue;

                for (int i = 0; i < source.Lines.Length; i++)
                {
                    var line = source.Lines[i];
                    if (DoctorContext.IsSuppressed(line))
                        continue;
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///"))
                        continue;

                    if (LoopStart.IsMatch(line))
                    {
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                            "DataProvider starts a loop not keyed on LifetimeToken/ShutdownToken — the loop will outlive Deactivate(); thread the lifetime token and exit on cancel.",
                            source.Path, i + 1));
                        break; // one finding per file is enough
                    }
                }
            }
            return issues;
        }
    }
}
