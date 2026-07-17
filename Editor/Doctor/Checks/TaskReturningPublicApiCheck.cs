using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Flags a public method declared to return <c>Task</c>/<c>Task&lt;T&gt;</c>.
    /// Per <c>.claude/async-contract.md</c> rule 1, async runtime APIs return
    /// <c>Awaitable</c>/<c>Awaitable&lt;T&gt;</c> — never <c>Task</c> (allocation and
    /// scheduler mismatch with Unity's player loop).
    /// </summary>
    /// <remarks>
    /// Text-only heuristic; deliberately restricted to <c>public</c> declarations so
    /// intentional private <c>Task</c>-returning helpers used for coalescing (e.g.
    /// <c>TaskCompletionSource</c>-based join points) are unaffected. Reported as
    /// Warning; suppress an intentional case with `doctor:ignore`.
    /// </remarks>
    public class TaskReturningPublicApiCheck : IDoctorCheck
    {
        public string Id => "task-returning-public-api";
        public string Description => "Public API returns Task/Task<T> instead of Awaitable/Awaitable<T>";

        private static readonly Regex Decl = new Regex(
            @"\bpublic\s+(?:static\s+)?(?:virtual\s+|override\s+|async\s+)*(?:System\.Threading\.Tasks\.)?Task(?:<[^>]+>)?\s+(\w+)\s*\(");

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            await Awaitable.BackgroundThreadAsync();

            var issues = new List<DoctorIssue>();
            foreach (var source in context.RuntimeSources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (int i = 0; i < source.Lines.Length; i++)
                {
                    var line = source.Lines[i];
                    if (DoctorContext.IsSuppressed(line))
                        continue;
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///"))
                        continue;

                    var match = Decl.Match(line);
                    if (!match.Success)
                        continue;

                    issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                        $"'{match.Groups[1].Value}' is a public API returning Task — return Awaitable/Awaitable<T> instead (async-contract.md rule 1).",
                        source.Path, i + 1));
                }
            }
            return issues;
        }
    }
}
