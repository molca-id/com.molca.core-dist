using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Flags <c>RuntimeSubsystem.Initialize(Action&lt;IRuntimeSubsystem&gt;)</c> overrides
    /// whose body never references <c>finishCallback</c> — such a subsystem stalls
    /// bootstrap until the 20s init timeout. Subsystems overriding
    /// <c>InitializeAsync</c> instead are exempt.
    /// </summary>
    public class MissingFinishCallbackCheck : IDoctorCheck
    {
        public string Id => "missing-finish-callback";
        public string Description => "Initialize(finishCallback) overrides that never invoke the callback";

        private static readonly Regex InitializeDecl =
            new Regex(@"override\s+void\s+Initialize\s*\(\s*(?:System\.)?Action<IRuntimeSubsystem>\s*(\w+)");

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            // Pure text scan — run off the main thread so the editor stays responsive.
            await Awaitable.BackgroundThreadAsync();

            var issues = new List<DoctorIssue>();
            foreach (var source in context.RuntimeSources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (int i = 0; i < source.Lines.Length; i++)
                {
                    var match = InitializeDecl.Match(source.Lines[i]);
                    if (!match.Success || DoctorContext.IsSuppressed(source.Lines[i]))
                        continue;

                    string callbackName = match.Groups[1].Value;
                    if (!BodyMentions(source.Lines, i, callbackName))
                    {
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Error,
                            $"Initialize override never references `{callbackName}` — bootstrap will block until the init timeout. Invoke it on every code path (or override InitializeAsync instead).",
                            source.Path, i + 1));
                    }
                }
            }
            return issues;
        }

        // Brace-matched scan of the method body for the callback identifier.
        private static bool BodyMentions(string[] lines, int declLine, string identifier)
        {
            int depth = 0;
            bool entered = false;
            for (int i = declLine; i < lines.Length; i++)
            {
                foreach (char c in lines[i])
                {
                    if (c == '{') { depth++; entered = true; }
                    else if (c == '}') depth--;
                }

                // Skip the declaration line itself (it names the parameter).
                if (i > declLine && lines[i].Contains(identifier))
                    return true;

                if (entered && depth <= 0)
                    return false;
            }
            return false;
        }
    }
}
