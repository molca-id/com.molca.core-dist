using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Heuristic: flags a mutation (<c>.Add(</c>/<c>.Remove(</c>/<c>.RemoveAt(</c>/
    /// <c>.Insert(</c>/<c>.Clear(</c>) of a <c>[SerializeField]</c> collection field
    /// (<c>List&lt;T&gt;</c>/array/<c>Dictionary&lt;,&gt;</c>) on a <c>ScriptableObject</c>
    /// subclass, when the enclosing method contains no runtime guard. Config SOs are
    /// read-only config; a serialized collection written at runtime persists in the
    /// editor and silently diverges in a built player (the systemic pattern behind
    /// Sprint 85's AudioLibrary/AudioCollection/DialogAudioCollection/ColorModule fixes).
    /// </summary>
    /// <remarks>
    /// "Guarded" is a generous text-level heuristic: the enclosing method body
    /// containing any of <c>Application.isPlaying</c>, an <c>IsRuntime</c>-named guard,
    /// or an <c>#if UNITY_EDITOR</c> block is treated as already handling the rule —
    /// this deliberately keeps the check quiet on the current, already-fixed call sites
    /// while still catching a genuinely unguarded future regression. Reported as
    /// Warning; suppress an intentional case with `doctor:ignore`.
    /// </remarks>
    public class RuntimeSoCollectionWriteCheck : IDoctorCheck
    {
        public string Id => "runtime-so-collection-write";
        public string Description => "Unguarded mutation of a serialized collection field on a ScriptableObject subclass";

        private static readonly Regex ClassDecl = new Regex(@"\bclass\s+(\w+)(?:\s*:\s*([\w\.]+))?");

        private static readonly Regex CollectionField = new Regex(
            @"\[SerializeField[^\]]*\]\s*(?:private|protected|internal|public)?\s*(?:List<[\w<>\.\[\],\s]+>|[\w\.]+\[\]|Dictionary<[\w<>\.\[\],\s]+>)\s+(\w+)\s*[=;]");

        private static readonly HashSet<string> KnownCompliantRoots = new HashSet<string>
        {
            "MonoBehaviour", "EditorWindow", "Editor", "PropertyDrawer",
        };

        private static readonly string[] GuardMarkers =
        {
            "Application.isPlaying", "IsRuntime", "#if UNITY_EDITOR",
        };

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            await Awaitable.BackgroundThreadAsync();

            var sources = context.RuntimeSources.ToList();

            var classBase = new Dictionary<string, string>();
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var line in source.Lines)
                {
                    var m = ClassDecl.Match(line);
                    if (!m.Success) continue;
                    string name = m.Groups[1].Value;
                    string baseName = m.Groups[2].Success ? StripGeneric(m.Groups[2].Value) : null;
                    if (!classBase.ContainsKey(name))
                        classBase[name] = baseName;
                }
            }

            var issues = new List<DoctorIssue>();
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string currentClass = null;
                var fieldsInClass = new HashSet<string>();
                for (int i = 0; i < source.Lines.Length; i++)
                {
                    var line = source.Lines[i];

                    var classMatch = ClassDecl.Match(line);
                    if (classMatch.Success)
                    {
                        currentClass = classMatch.Groups[1].Value;
                        fieldsInClass.Clear();
                        if (IsScriptableObject(currentClass, classBase))
                            CollectFields(source.Lines, i, fieldsInClass);
                    }

                    if (currentClass == null || fieldsInClass.Count == 0)
                        continue;
                    if (DoctorContext.IsSuppressed(line))
                        continue;
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///"))
                        continue;
                    if (line.Contains("[SerializeField"))
                        continue; // the declaration line itself

                    foreach (var field in fieldsInClass)
                    {
                        if (!Regex.IsMatch(line, $@"\b{Regex.Escape(field)}\s*\.\s*(Add|Remove|RemoveAt|Insert|Clear)\s*\("))
                            continue;

                        string body = CollectEnclosingMethod(source.Lines, i);
                        if (GuardMarkers.Any(body.Contains))
                            continue; // treated as already handling the read-only-SO rule

                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                            $"Unguarded mutation of serialized collection '{field}' on ScriptableObject '{currentClass}' — " +
                            "gate this behind a runtime check (Application.isPlaying / a testable guard) so it stays an edit-time authoring operation only.",
                            source.Path, i + 1));
                    }
                }
            }
            return issues;
        }

        // Fields stay in scope only for the class body they were declared in — collect
        // from the declaration point up to the next top-level class boundary or EOF.
        private static void CollectFields(string[] lines, int fromLine, HashSet<string> sink)
        {
            for (int i = fromLine; i < lines.Length; i++)
            {
                if (i > fromLine && ClassDecl.IsMatch(lines[i]))
                    break;
                var m = CollectionField.Match(lines[i]);
                if (m.Success)
                    sink.Add(m.Groups[1].Value);
            }
        }

        private static bool IsScriptableObject(string className, Dictionary<string, string> classBase)
        {
            if (!classBase.TryGetValue(className, out var baseName) || string.IsNullOrEmpty(baseName))
                return false;
            var visited = new HashSet<string> { className };
            string hop = baseName;
            for (int i = 0; i < 8; i++)
            {
                if (hop == "ScriptableObject") return true;
                if (KnownCompliantRoots.Contains(hop)) return false; // a MonoBehaviour etc — different rule
                if (!visited.Add(hop)) return false;
                if (!classBase.TryGetValue(hop, out var next) || string.IsNullOrEmpty(next)) return false;
                hop = next;
            }
            return false;
        }

        // Walks outward from `line` to the nearest enclosing method's braces (bounded),
        // returning its text for the guard-marker substring scan.
        private static string CollectEnclosingMethod(string[] lines, int line)
        {
            int start = line;
            int depth = 0;
            for (int i = line; i >= 0 && i >= line - 200; i--)
            {
                foreach (char c in lines[i])
                {
                    if (c == '}') depth++;
                    else if (c == '{') depth--;
                }
                start = i;
                if (depth < 0) break; // found the method's opening brace
            }

            var sb = new System.Text.StringBuilder();
            int d = 0;
            bool started = false;
            for (int i = start; i < lines.Length && i < start + 200; i++)
            {
                sb.Append(lines[i]).Append('\n');
                foreach (char c in lines[i])
                {
                    if (c == '{') { d++; started = true; }
                    else if (c == '}') d--;
                }
                if (started && d <= 0) break;
            }
            return sb.ToString();
        }

        private static string StripGeneric(string token)
        {
            int lt = token.IndexOf('<');
            return lt >= 0 ? token.Substring(0, lt) : token;
        }
    }
}
