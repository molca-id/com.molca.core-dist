using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Heuristic: flags a public <c>async void</c> method that is not a recognized
    /// Unity event-handler entry point. Per <c>.claude/async-contract.md</c> rule 2,
    /// <c>async void</c> is reserved for entry points (<c>Start</c>/<c>Awake</c>/UI
    /// callbacks/<c>[RuntimeInitializeOnLoadMethod]</c>); anywhere else an unobserved
    /// exception escapes into Unity's synchronization context.
    /// </summary>
    /// <remarks>
    /// Text-only heuristic — it cannot see whether an <c>async void</c> shim wraps its
    /// body in try/catch (the contract's compat-shim exception, rule 2). Reported as
    /// Warning; an intentional shim should be marked `doctor:ignore` at the declaration.
    /// </remarks>
    public class AsyncVoidNonEntryPointCheck : IDoctorCheck
    {
        public string Id => "async-void-non-entrypoint";
        public string Description => "Public async void method that is not a recognized Unity entry point";

        private static readonly HashSet<string> EntryPointNames = new HashSet<string>
        {
            "Awake", "Start", "OnEnable", "OnDisable", "OnDestroy", "OnValidate",
            "Update", "FixedUpdate", "LateUpdate", "OnGUI",
            "OnDrawGizmos", "OnDrawGizmosSelected",
            "OnMouseDown", "OnMouseUp", "OnMouseEnter", "OnMouseExit", "OnMouseOver",
            "OnCollisionEnter", "OnCollisionExit", "OnCollisionStay",
            "OnTriggerEnter", "OnTriggerExit", "OnTriggerStay",
            "OnApplicationFocus", "OnApplicationPause", "OnApplicationQuit",
        };

        private static readonly Regex Decl = new Regex(@"\bpublic\s+(?:static\s+)?async\s+void\s+(\w+)\s*\(");

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

                    string name = match.Groups[1].Value;
                    if (EntryPointNames.Contains(name))
                        continue;

                    // A [RuntimeInitializeOnLoadMethod] on the immediately preceding
                    // non-blank line is a recognized entry point too.
                    if (PrecedingLineHasAttribute(source.Lines, i, "RuntimeInitializeOnLoadMethod"))
                        continue;

                    issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                        $"'{name}' is public async void but not a recognized Unity entry point — return Awaitable with a CancellationToken, " +
                        "or if this must stay async void for API compatibility, wrap the body in try/catch and mark this line `doctor:ignore` with justification.",
                        source.Path, i + 1));
                }
            }
            return issues;
        }

        private static bool PrecedingLineHasAttribute(string[] lines, int index, string attributeName)
        {
            for (int j = index - 1; j >= 0 && j >= index - 3; j--)
            {
                var t = lines[j].Trim();
                if (t.Length == 0) continue;
                return t.Contains(attributeName);
            }
            return false;
        }
    }
}
