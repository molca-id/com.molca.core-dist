using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Flags two misuses of <see cref="Molca.Localization.DynamicLocalization"/> in
    /// consuming source:
    /// <list type="bullet">
    /// <item>The fire-and-forget <c>Init(...)</c> immediately followed (same method) by
    /// <c>await ….GetLocalizedString()</c> on the same field. <c>Init</c> is
    /// <c>async void</c>, so it races the resolve and the string often comes back empty —
    /// await <c>InitAsync(...)</c> instead.</item>
    /// <item>A field read through <c>.String</c> / <c>.GetLocalizedString()</c> that is
    /// never initialized (<c>Init</c>/<c>InitAsync</c>) anywhere in the file — the
    /// required registration call is missing, so it only ever returns authored fallback.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Pure text scan, so it runs off the main thread. Warning severity — a text scan
    /// cannot prove a field is initialized in a base class or a helper, nor that an
    /// identifier truly holds a <c>DynamicLocalization</c> in every case. Suppress a
    /// specific line with the <c>doctor:ignore</c> marker.
    /// </remarks>
    public class DynamicLocalizationInitContractCheck : IDoctorCheck
    {
        public string Id => "dynamic-localization-init-contract";
        public string Description => "DynamicLocalization resolved without (or racing) its required Init call";

        // Field/local declared as DynamicLocalization, capturing its identifier. Generic
        // declarations (List<DynamicLocalization>) end in '>' and are not matched.
        private static readonly Regex FieldDecl =
            new Regex(@"\bDynamicLocalization\s+(@?\w+)\s*(?:[=;,)]|\bwhere\b)");

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            await Awaitable.BackgroundThreadAsync();

            var issues = new List<DoctorIssue>();
            foreach (var source in context.RuntimeSources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Pass 1: collect identifiers declared as DynamicLocalization in this file.
                var names = new HashSet<string>();
                foreach (var line in source.Lines)
                {
                    var m = FieldDecl.Match(line);
                    if (m.Success)
                        names.Add(m.Groups[1].Value);
                }
                if (names.Count == 0)
                    continue;

                var group = string.Join("|", names);
                var syncInit = new Regex($@"\b({group})\s*\.\s*Init\s*\(");
                var anyInit = new Regex($@"\b({group})\s*\.\s*Init(?:Async)?\s*\(");
                var resolve = new Regex($@"\b({group})\s*\.\s*(?:GetLocalizedString\s*\(|String\b)");

                // Per-name line bookkeeping for whole-file rules.
                var firstResolveLine = new Dictionary<string, int>();
                var initedNames = new HashSet<string>();

                for (int i = 0; i < source.Lines.Length; i++)
                {
                    var line = source.Lines[i];
                    if (DoctorContext.IsSuppressed(line))
                        continue;

                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///"))
                        continue;

                    foreach (var im in anyInit.Matches(line))
                        initedNames.Add(((Match)im).Groups[1].Value);

                    foreach (var rm in resolve.Matches(line))
                    {
                        var name = ((Match)rm).Groups[1].Value;
                        if (!firstResolveLine.ContainsKey(name))
                            firstResolveLine[name] = i;
                    }

                    // Race rule: a synchronous Init followed by a GetLocalizedString on the
                    // same identifier, anywhere later in the same method window. We approximate
                    // "same method" with a forward look until the resolve appears, which is
                    // sufficient for the common Start()/OnInitialize() pattern.
                    var syncMatch = syncInit.Match(line);
                    if (syncMatch.Success)
                    {
                        var name = syncMatch.Groups[1].Value;
                        var nameResolve = new Regex($@"\b{Regex.Escape(name)}\s*\.\s*GetLocalizedString\s*\(");
                        for (int j = i + 1; j < source.Lines.Length && j <= i + 12; j++)
                        {
                            if (DoctorContext.IsSuppressed(source.Lines[j]))
                                continue;
                            if (nameResolve.IsMatch(source.Lines[j]))
                            {
                                issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                                    $"`{name}.Init(...)` is fire-and-forget and races the following " +
                                    $"`{name}.GetLocalizedString()` — the string may resolve empty. " +
                                    "Await `InitAsync(...)` before resolving.",
                                    source.Path, j + 1));
                                break;
                            }
                        }
                    }
                }

                // Missing-init rule: a field that is resolved but never initialized anywhere.
                foreach (var pair in firstResolveLine)
                {
                    if (initedNames.Contains(pair.Key))
                        continue;
                    issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                        $"`{pair.Key}` is read (.String / .GetLocalizedString()) but never initialized " +
                        "with Init/InitAsync in this file — it will only return authored fallback text.",
                        source.Path, pair.Value + 1));
                }
            }

            return issues;
        }
    }
}
