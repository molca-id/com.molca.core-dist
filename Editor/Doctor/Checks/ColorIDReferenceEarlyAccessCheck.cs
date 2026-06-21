using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Flags reads of a <see cref="Molca.ColorID.ColorIDReference"/>'s resolved color
    /// (the <c>.Color</c> / <c>.GetColorWithAlpha</c> members) inside Unity entry points
    /// that run before bootstrap — <c>Awake</c>, <c>OnEnable</c>, <c>OnValidate</c>.
    /// Resolving a color calls <c>ColorModule.ResolveActive()</c>, which is unreliable
    /// until <c>GlobalSettings</c> is initialized; read it after
    /// <c>RuntimeManager.WaitForInitialization()</c> instead. Warning severity — a text
    /// scan cannot prove the field truly holds a ColorIDReference in every case.
    /// </summary>
    public class ColorIDReferenceEarlyAccessCheck : IDoctorCheck
    {
        public string Id => "color-id-reference-early-access";
        public string Description => "ColorIDReference color read in Awake/OnEnable/OnValidate (before ColorModule init)";

        // Field/local declared as ColorIDReference, capturing its identifier.
        // Matches e.g. "[SerializeField] private ColorIDReference _bg;" and "ColorIDReference c = ...".
        private static readonly Regex FieldDecl =
            new Regex(@"\bColorIDReference\s+(@?\w+)\s*(?:[=;,)]|\bwhere\b)");

        // Entry points that run before RuntimeManager finishes bootstrap; group 1 = name.
        private static readonly Regex UnsafeMethodStart =
            new Regex(@"^\s*(?:\[[^\]]*\]\s*)*(?:public|private|protected|internal)?\s*(?:async\s+)?(?:void|Awaitable)\s+(Awake|OnEnable|OnValidate)\s*\(");

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            // Pure text scan — run off the main thread so the editor stays responsive.
            await Awaitable.BackgroundThreadAsync();

            var issues = new List<DoctorIssue>();
            foreach (var source in context.RuntimeSources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Pass 1: collect identifiers declared as ColorIDReference in this file.
                var names = new HashSet<string>();
                foreach (var line in source.Lines)
                {
                    var m = FieldDecl.Match(line);
                    if (m.Success)
                        names.Add(m.Groups[1].Value);
                }
                if (names.Count == 0)
                    continue;

                // A read of the resolved color through one of the collected identifiers.
                var colorRead = new Regex(
                    $@"\b({string.Join("|", names)})\s*\.\s*(?:Color\b|GetColorWithAlpha\s*\()");

                // Pass 2: flag color reads inside unsafe entry points.
                for (int i = 0; i < source.Lines.Length; i++)
                {
                    if (!UnsafeMethodStart.IsMatch(source.Lines[i]))
                        continue;

                    var methodName = UnsafeMethodStart.Match(source.Lines[i]).Groups[1].Value;
                    int depth = 0;
                    bool entered = false;

                    for (int j = i; j < source.Lines.Length; j++)
                    {
                        var bodyLine = source.Lines[j];
                        depth += CountUnquoted(bodyLine, '{') - CountUnquoted(bodyLine, '}');
                        if (depth > 0)
                            entered = true;

                        if (j > i && !DoctorContext.IsSuppressed(bodyLine))
                        {
                            var trimmed = bodyLine.TrimStart();
                            bool isComment = trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///");
                            if (!isComment && colorRead.IsMatch(bodyLine))
                            {
                                issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                                    $"ColorIDReference color read inside {methodName}() — ColorModule may not be initialized yet. Read it after RuntimeManager.WaitForInitialization().",
                                    source.Path, j + 1));
                            }
                        }

                        if (entered && depth <= 0)
                            break; // method body closed
                    }
                }
            }
            return issues;
        }

        // Counts a character outside of string/char literals so braces inside text don't
        // skew brace-depth tracking. A line-level "//" comment ends the scan for the line.
        private static int CountUnquoted(string line, char ch)
        {
            int count = 0;
            bool inString = false, inChar = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (!inString && !inChar && c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                    break; // rest of line is a comment
                if (c == '"' && !inChar && (i == 0 || line[i - 1] != '\\')) inString = !inString;
                else if (c == '\'' && !inString && (i == 0 || line[i - 1] != '\\')) inChar = !inChar;
                else if (!inString && !inChar && c == ch) count++;
            }
            return count;
        }
    }
}
