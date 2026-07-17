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

                string currentClass = null;
                foreach (var m in source.Lines.Select((line, i) => (line, i)))
                {
                    var classMatch = ClassDecl.Match(m.line);
                    if (classMatch.Success)
                        currentClass = classMatch.Groups[1].Value;

                    if (currentClass == null || DoctorContext.IsSuppressed(m.line))
                        continue;
                    var trimmed = m.line.TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///"))
                        continue;

                    var methodMatch = MethodDecl.Match(m.line);
                    if (!methodMatch.Success)
                        continue;

                    string methodName = methodMatch.Groups[1].Value;
                    string root = ResolveRoot(currentClass, classBase);

                    if (root == null)
                        continue; // unresolved external base — can't prove anything

                    bool isMbOnly = MonoBehaviourOnly.Contains(methodName);
                    bool flag = root switch
                    {
                        "" => true, // no base at all — never Unity-managed
                        "ScriptableObject" => isMbOnly, // shared methods DO fire on SO
                        _ => false, // MonoBehaviour or another known-compliant root
                    };

                    if (flag)
                    {
                        string advice = root == ""
                            ? "no base class at all — Unity never calls a lifecycle method here; drive it explicitly (e.g. an interface hook the owner calls, or an Awaitable loop)."
                            : "a ScriptableObject never receives this MonoBehaviour-only callback — drive it with an explicit Awaitable pump loop keyed on a lifetime token instead (see WebSocketDataProvider.PumpLoopAsync).";
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                            $"'{currentClass}.{methodName}()' is dead: {advice}",
                            source.Path, m.i + 1));
                    }
                }
            }
            return issues;
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
