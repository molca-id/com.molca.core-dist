using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Heuristic: flags a Unity lifecycle method name (Awake, Update, OnEnable, ...)
    /// declared on a type Unity will never call it on — a plain C# class (no base at
    /// all) or a <c>ScriptableObject</c> subclass using a MonoBehaviour-only callback
    /// (Update/FixedUpdate/OnGUI/collision-trigger-mouse events/Start). This is exactly
    /// the shape of two Sprint 78/79 criticals: <c>StepAuxiliary</c> subclasses with a
    /// dead <c>Awake()</c>, and a <c>ScriptableObject</c> data provider expecting
    /// <c>Update()</c> to be called by Unity (it never is on an SO).
    /// </summary>
    /// <remarks>
    /// Text-only heuristic: base-class resolution only follows chains built from types
    /// declared within the scanned sources, so it never asserts about an unresolvable
    /// external base — false negatives are preferred over false positives. Reported as
    /// Warning; suppress an intentional case with a `doctor:ignore` comment.
    /// </remarks>
    public class UnityLifecycleWrongTypeCheck : IDoctorCheck
    {
        public string Id => "unity-lifecycle-wrong-type";
        public string Description => "Unity lifecycle method declared on a type Unity will never call it on";

        private static readonly Regex ClassDecl = new Regex(
            @"\bclass\s+(\w+)(?:\s*:\s*([\w\.]+))?");

        // Never called on ScriptableObject OR on a plain (non-Unity) class.
        private static readonly string[] MonoBehaviourOnly =
        {
            "Start", "Update", "FixedUpdate", "LateUpdate", "OnGUI",
            "OnDrawGizmos", "OnDrawGizmosSelected",
            "OnMouseDown", "OnMouseUp", "OnMouseEnter", "OnMouseExit", "OnMouseOver",
            "OnCollisionEnter", "OnCollisionExit", "OnCollisionStay",
            "OnCollisionEnter2D", "OnCollisionExit2D", "OnCollisionStay2D",
            "OnTriggerEnter", "OnTriggerExit", "OnTriggerStay",
            "OnTriggerEnter2D", "OnTriggerExit2D", "OnTriggerStay2D",
            "OnApplicationFocus", "OnApplicationPause", "OnApplicationQuit",
        };

        // Called on both MonoBehaviour and ScriptableObject; still never called on a
        // plain class with no Unity base at all.
        private static readonly string[] SharedMonoOrSo =
        {
            "Awake", "OnEnable", "OnDisable", "OnDestroy", "OnValidate",
        };

        // Known Unity/editor base types that receive some lifecycle-shaped callbacks
        // under their own rules — resolving to one of these is always compliant.
        private static readonly HashSet<string> KnownCompliantRoots = new HashSet<string>
        {
            "MonoBehaviour", "ScriptableObject", "EditorWindow", "Editor", "PropertyDrawer",
            "ScriptableWizard", "StateMachineBehaviour", "AssetPostprocessor",
            "AssetModificationProcessor", "ScriptedImporter",
        };

        private static readonly Regex MethodDecl;

        static UnityLifecycleWrongTypeCheck()
        {
            var all = MonoBehaviourOnly.Concat(SharedMonoOrSo);
            MethodDecl = new Regex($@"\bvoid\s+({string.Join("|", all)})\s*\(");
        }

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

            // className -> first base token (null when the class has no base at all).
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

                // Track the *innermost enclosing class* by brace depth. The previous
                // implementation only latched `currentClass` on each `class` line and
                // never popped it when a nested type's scope closed, so an outer type's
                // real lifecycle methods (e.g. AudioManager.OnDestroy declared after a
                // nested LoadedClipRecord) were misattributed to the last-seen nested
                // class — the source of a wave of false positives.
                var scope = new Stack<(string name, int openDepth)>();
                int depth = 0;
                string pendingClass = null;   // class awaiting its opening brace
                int pendingClassMinIndex = 0; // that brace must appear at/after this column
                bool inBlockComment = false;

                foreach (var m in source.Lines.Select((line, i) => (line, i)))
                {
                    string clean = StripNonCode(m.line, ref inBlockComment);

                    var classMatch = ClassDecl.Match(clean);
                    if (classMatch.Success)
                    {
                        pendingClass = classMatch.Groups[1].Value;
                        pendingClassMinIndex = classMatch.Index;
                    }

                    string currentClass = scope.Count > 0 ? scope.Peek().name : null;

                    if (currentClass != null && !DoctorContext.IsSuppressed(m.line))
                        ClassifyMethodLine(clean, currentClass, classBase, source.Path, m.i, issues);

                    UpdateScope(clean, ref depth, scope, ref pendingClass, ref pendingClassMinIndex);
                }
            }
            return issues;
        }

        // Applies the lifecycle-method heuristic to a single (already comment/string-stripped)
        // line, given the class it is lexically inside.
        private void ClassifyMethodLine(string clean, string currentClass,
            Dictionary<string, string> classBase, string path, int lineIndex, List<DoctorIssue> issues)
        {
            var trimmed = clean.TrimStart();
            if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///"))
                return;

            var methodMatch = MethodDecl.Match(clean);
            if (!methodMatch.Success)
                return;

            string methodName = methodMatch.Groups[1].Value;
            string root = ResolveRoot(currentClass, classBase);

            if (root == null)
                return; // unresolved external base — can't prove anything

            bool isMbOnly = MonoBehaviourOnly.Contains(methodName);
            bool flag = root switch
            {
                "" => true, // no base at all — never Unity-managed
                "ScriptableObject" => isMbOnly, // shared methods DO fire on SO
                _ => false, // MonoBehaviour or another known-compliant root
            };

            if (!flag)
                return;

            string advice = root == ""
                ? "no base class at all — Unity never calls a lifecycle method here; drive it explicitly (e.g. an interface hook the owner calls, or an Awaitable loop)."
                : "a ScriptableObject never receives this MonoBehaviour-only callback — drive it with an explicit Awaitable pump loop keyed on a lifetime token instead (see WebSocketDataProvider.PumpLoopAsync).";
            issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                $"'{currentClass}.{methodName}()' is dead: {advice}",
                path, lineIndex + 1));
        }

        // Advances brace depth and the enclosing-class stack across one stripped line.
        // A `class` declaration is remembered as pending and pushed only when its own
        // opening brace is reached (a brace at/after the `class` keyword's column, or any
        // brace on a following line) — so a namespace `{` sharing the declaration line
        // never gets mistaken for the class body.
        private static void UpdateScope(string clean, ref int depth,
            Stack<(string name, int openDepth)> scope, ref string pendingClass, ref int pendingClassMinIndex)
        {
            for (int col = 0; col < clean.Length; col++)
            {
                char c = clean[col];
                if (c == '{')
                {
                    depth++;
                    if (pendingClass != null && col >= pendingClassMinIndex)
                    {
                        scope.Push((pendingClass, depth));
                        pendingClass = null;
                    }
                }
                else if (c == '}')
                {
                    if (scope.Count > 0 && scope.Peek().openDepth == depth)
                        scope.Pop();
                    if (depth > 0)
                        depth--;
                }
            }

            // A class whose brace is on a later line: any brace there opens it.
            if (pendingClass != null)
                pendingClassMinIndex = 0;
        }

        // Blanks out comment and string/char-literal content so brace/keyword scanning
        // sees only code. Tracks `/* */` block-comment state across lines. Verbatim/
        // interpolated strings are handled approximately (whole literal body blanked),
        // which is enough to keep braces inside strings from skewing the depth count.
        private static string StripNonCode(string line, ref bool inBlockComment)
        {
            var sb = new System.Text.StringBuilder(line.Length);
            int i = 0;
            while (i < line.Length)
            {
                if (inBlockComment)
                {
                    int end = line.IndexOf("*/", i, StringComparison.Ordinal);
                    if (end < 0) return sb.ToString();
                    inBlockComment = false;
                    i = end + 2;
                    continue;
                }

                char c = line[i];
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                    break; // line comment — rest is non-code
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
                    sb.Append(' '); // placeholder so token boundaries survive
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

        // Walks the base-class chain built from scanned sources only. Returns "" for no
        // base at all, a known root name ("MonoBehaviour"/"ScriptableObject"/other known
        // compliant root) when reached, or null when the chain leads somewhere we can't
        // resolve (external/unscanned type) — treated as "unknown, don't flag".
        private static string ResolveRoot(string className, Dictionary<string, string> classBase)
        {
            if (!classBase.TryGetValue(className, out var baseName))
                return null; // the class itself wasn't captured by the declaration regex
            if (string.IsNullOrEmpty(baseName))
                return "";

            var visited = new HashSet<string> { className };
            string hop = baseName;
            for (int i = 0; i < 8; i++)
            {
                if (KnownCompliantRoots.Contains(hop))
                    return hop;
                if (!visited.Add(hop))
                    return null; // cycle — bail out rather than loop forever
                if (!classBase.TryGetValue(hop, out var next))
                    return null; // external/unscanned base — unknown
                if (string.IsNullOrEmpty(next))
                    return null; // hop is itself a plain class with no further base — ambiguous, skip
                hop = next;
            }
            return null;
        }

        private static string StripGeneric(string token)
        {
            int lt = token.IndexOf('<');
            return lt >= 0 ? token.Substring(0, lt) : token;
        }
    }
}
