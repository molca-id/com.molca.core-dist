using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Heuristic: flags assignments to [SerializeField] members inside
    /// <c>SettingModule</c> subclasses outside editor-only regions. Per the settings
    /// cardinal rule, runtime-mutable values belong on a paired <c>SettingState</c>.
    /// Reported as Warning because text-level analysis cannot prove the assignment
    /// runs at runtime; suppress intentional cases with a `doctor:ignore` comment.
    /// </summary>
    public class RuntimeSoWriteCheck : IDoctorCheck
    {
        public string Id => "runtime-so-write";
        public string Description => "SerializeField writes in SettingModule subclasses (SO cardinal rule)";

        private static readonly Regex ClassDecl = new Regex(@"\bclass\s+\w+\s*:\s*(?:\w+\s*,\s*)*SettingModule\b");
        private static readonly Regex SerializeField = new Regex(@"\[SerializeField[^\]]*\]\s*(?:private|protected|internal)?\s*[\w<>\[\],\s\.]+?\s(\w+)\s*(?:=|;)");

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            // Pure text scan — run off the main thread so the editor stays responsive.
            await Awaitable.BackgroundThreadAsync();

            var issues = new List<DoctorIssue>();
            foreach (var source in context.RuntimeSources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!source.Lines.Any(l => ClassDecl.IsMatch(l)))
                    continue;

                // Collect serialize-field names declared in this file.
                var fields = new HashSet<string>();
                foreach (var line in source.Lines)
                {
                    var match = SerializeField.Match(line);
                    if (match.Success)
                        fields.Add(match.Groups[1].Value);
                }
                if (fields.Count == 0)
                    continue;

                // The declaration guard rejects locals, out-vars, and default parameter
                // values that merely share a serialized field's name
                // (`Color color = ...`, `string description = ""`).
                var assignment = new Regex(
                    $@"(?<![\w\.])(?<!(?:var|out|ref|string|bool|int|uint|long|float|double|Color|Sprite|Vector\d)\s+)({string.Join("|", fields)})\s*=(?![=>])");

                int editorIfDepth = 0;
                for (int i = 0; i < source.Lines.Length; i++)
                {
                    var line = source.Lines[i];
                    var trimmed = line.TrimStart();

                    // Editor-only regions may freely author the asset.
                    if (trimmed.StartsWith("#if") && line.Contains("UNITY_EDITOR")) { editorIfDepth++; continue; }
                    if (trimmed.StartsWith("#endif") && editorIfDepth > 0) { editorIfDepth--; continue; }
                    if (editorIfDepth > 0)
                        continue;

                    if (DoctorContext.IsSuppressed(line))
                        continue;
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///"))
                        continue;
                    // Field declarations initialize their own defaults.
                    if (line.Contains("[SerializeField") || trimmed.StartsWith("[SerializeField"))
                        continue;

                    var match = assignment.Match(line);
                    if (match.Success)
                    {
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                            $"SerializeField `{match.Groups[1].Value}` is assigned outside an editor-only region — move the mutable value to the paired SettingState.",
                            source.Path, i + 1));
                    }
                }
            }
            return issues;
        }
    }
}
