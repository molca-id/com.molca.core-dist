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
            // Hop back before completing: an Awaitable finished on the ThreadPool thread this scan ran
            // on raises a native "Scripting object is not properly attached" assert (see IDoctorCheck).
            try { return Scan(context, cancellationToken); }
            finally { await Awaitable.MainThreadAsync(); }
        }

        private IReadOnlyList<DoctorIssue> Scan(DoctorContext context, CancellationToken cancellationToken)
        {

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

                // Comment/string-stripped copy, used only for brace counting in the
                // enclosing-method scan — an interpolated string such as
                // $"removed {collection.name}" must not skew the brace balance.
                string[] cleanLines = CleanAll(source.Lines);

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

                        string body = CollectEnclosingMethod(source.Lines, cleanLines, i);
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

        // Trimmed leading token of a control-flow block (as opposed to a method/property
        // scope). A guard at the top of a method must be found even when the mutation is
        // nested inside if/for/while blocks, so the scan walks *out* of these to the
        // enclosing method scope.
        private static readonly Regex ControlBlockHeader = new Regex(
            @"^\s*(\}\s*)?\b(if|else|for|foreach|while|switch|using|lock|catch|try|do|fixed|unsafe)\b");

        // Walks outward from `line` to the nearest enclosing *method* scope — skipping
        // inner control-flow blocks (if/for/…) so a runtime guard at the method's top is
        // included — and returns its raw text for the guard-marker substring scan.
        // Brace counting runs on the comment/string-stripped `cleanLines`; the returned
        // text is the original `rawLines` so guard tokens read normally.
        private static string CollectEnclosingMethod(string[] rawLines, string[] cleanLines, int line)
        {
            int start = 0;
            int depth = 0;
            for (int i = line; i >= 0 && i >= line - 500; i--)
            {
                var clean = cleanLines[i];
                bool foundMethodOpen = false;
                for (int col = clean.Length - 1; col >= 0; col--)
                {
                    char c = clean[col];
                    if (c == '}')
                    {
                        depth++;
                    }
                    else if (c == '{')
                    {
                        if (depth > 0) { depth--; continue; }
                        // depth == 0: this brace opens the scope directly containing our
                        // position. If it is a control block, keep walking outward; the
                        // method scope is the first non-control opener.
                        if (IsControlBlockOpen(cleanLines, i, col))
                            continue;
                        start = i;
                        foundMethodOpen = true;
                        break;
                    }
                }
                if (foundMethodOpen)
                    break;
                start = i;
            }

            var sb = new System.Text.StringBuilder();
            int d = 0;
            bool started = false;
            for (int i = start; i < rawLines.Length && i < start + 500; i++)
            {
                sb.Append(rawLines[i]).Append('\n');
                foreach (char c in cleanLines[i])
                {
                    if (c == '{') { d++; started = true; }
                    else if (c == '}') d--;
                }
                if (started && d <= 0) break;
            }
            return sb.ToString();
        }

        // True when the `{` at (braceLine, braceCol) opens a control-flow block rather
        // than a method/type body. Uses the header text before the brace, or the nearest
        // preceding non-empty line when the brace sits on its own line (Allman style).
        private static bool IsControlBlockOpen(string[] cleanLines, int braceLine, int braceCol)
        {
            string header = cleanLines[braceLine].Substring(0, braceCol);
            if (header.Trim().Length == 0)
            {
                for (int i = braceLine - 1; i >= 0 && i >= braceLine - 5; i--)
                {
                    if (cleanLines[i].Trim().Length == 0) continue;
                    header = cleanLines[i];
                    break;
                }
            }
            return ControlBlockHeader.IsMatch(header);
        }

        private static string[] CleanAll(string[] lines)
        {
            var result = new string[lines.Length];
            bool inBlockComment = false;
            for (int i = 0; i < lines.Length; i++)
                result[i] = StripNonCode(lines[i], ref inBlockComment);
            return result;
        }

        // Blanks comment and string/char-literal content so brace scanning sees only code.
        // Tracks `/* */` state across lines; string bodies are blanked wholesale, which is
        // enough to keep braces inside (including interpolated) strings out of the count.
        private static string StripNonCode(string line, ref bool inBlockComment)
        {
            var sb = new System.Text.StringBuilder(line.Length);
            int i = 0;
            while (i < line.Length)
            {
                if (inBlockComment)
                {
                    int end = line.IndexOf("*/", i, System.StringComparison.Ordinal);
                    if (end < 0) return sb.ToString();
                    inBlockComment = false;
                    i = end + 2;
                    continue;
                }

                char c = line[i];
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                    break;
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    inBlockComment = true;
                    i += 2;
                    continue;
                }
                if (c == '"')
                {
                    i++;
                    while (i < line.Length)
                    {
                        if (line[i] == '\\') { i += 2; continue; }
                        if (line[i] == '"') { i++; break; }
                        i++;
                    }
                    sb.Append(' ');
                    continue;
                }
                if (c == '\'')
                {
                    i++;
                    while (i < line.Length)
                    {
                        if (line[i] == '\\') { i += 2; continue; }
                        if (line[i] == '\'') { i++; break; }
                        i++;
                    }
                    sb.Append(' ');
                    continue;
                }

                sb.Append(c);
                i++;
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
