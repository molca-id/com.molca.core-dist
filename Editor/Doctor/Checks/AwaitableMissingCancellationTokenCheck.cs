using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Heuristic: flags a public <c>Awaitable</c>/<c>Awaitable&lt;T&gt;</c>-returning
    /// method whose body looks long-running (a loop, or an await on
    /// <c>WaitForSecondsAsync</c>/<c>NextFrameAsync</c>) but declares no
    /// <c>CancellationToken</c> parameter. Per <c>.claude/async-contract.md</c> rule 3,
    /// long-running work must accept one so a caller can abort it.
    /// </summary>
    /// <remarks>
    /// Deliberately scoped to loop-shaped bodies, not every public Awaitable method —
    /// a one-shot helper genuinely has nothing to cancel. Reported as Warning;
    /// suppress an intentional case with `doctor:ignore` on the declaration line.
    /// </remarks>
    public class AwaitableMissingCancellationTokenCheck : IDoctorCheck
    {
        public string Id => "awaitable-missing-cancellation-token";
        public string Description => "Long-running public Awaitable method with no CancellationToken parameter";

        private static readonly Regex Decl = new Regex(
            @"\bpublic\s+(?:static\s+)?(?:virtual\s+|override\s+)?async\s+Awaitable(?:<[^>]+>)?\s+(\w+)\s*\(");

        private static readonly Regex LongRunningSignal = new Regex(
            @"\bwhile\s*\(|\bfor\s*\(|WaitForSecondsAsync|NextFrameAsync|BackgroundThreadAsync");

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

                    // The parameter list may wrap onto following lines; join until parens balance.
                    var (signature, sigLine) = JoinSignature(source.Lines, i);
                    if (signature.Contains("CancellationToken"))
                        continue;

                    string body = CollectBody(source.Lines, sigLine);
                    if (!LongRunningSignal.IsMatch(body))
                        continue; // one-shot helper — nothing to cancel

                    issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                        $"'{match.Groups[1].Value}' looks long-running (loop/await-wait) but takes no CancellationToken — " +
                        "add one as the last parameter (default = default) and thread it through every await.",
                        source.Path, i + 1));
                }
            }
            return issues;
        }

        // Joins lines from `start` until the opening paren's depth returns to 0, bounded
        // to avoid runaway joins on malformed input. Returns the joined text and the
        // last line index consumed (so body collection resumes after the signature).
        private static (string text, int lastLine) JoinSignature(string[] lines, int start)
        {
            var sb = new StringBuilder();
            int depth = 0;
            bool seenOpen = false;
            int last = start;
            for (int i = start; i < lines.Length && i < start + 10; i++)
            {
                sb.Append(lines[i]).Append(' ');
                foreach (char c in lines[i])
                {
                    if (c == '(') { depth++; seenOpen = true; }
                    else if (c == ')') depth--;
                }
                last = i;
                if (seenOpen && depth <= 0)
                    break;
            }
            return (sb.ToString(), last);
        }

        // Collects the method body by brace depth, bounded, starting the scan at the
        // first '{' at/after sigLine. A heuristic, not a real parser — good enough to
        // detect the presence of a loop/wait construct.
        private static string CollectBody(string[] lines, int sigLine)
        {
            var sb = new StringBuilder();
            int depth = 0;
            bool started = false;
            for (int i = sigLine; i < lines.Length && i < sigLine + 200; i++)
            {
                sb.Append(lines[i]).Append('\n');
                foreach (char c in lines[i])
                {
                    if (c == '{') { depth++; started = true; }
                    else if (c == '}') depth--;
                }
                if (started && depth <= 0)
                    break;
            }
            return sb.ToString();
        }
    }
}
