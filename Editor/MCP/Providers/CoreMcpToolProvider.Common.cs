using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Shared plumbing used by every <c>CoreMcpToolProvider</c> partial file (argument parsing, error
    /// payloads, and JSON/<see cref="FieldNode"/> conversion) — mirrors <c>UnityMcpToolProvider.Common.cs</c>/
    /// <c>.Components.cs</c>'s own equivalents. Kept in its own file since it has no single natural home
    /// among the tool-specific partials.
    /// </summary>
    public partial class CoreMcpToolProvider
    {
        // Tools return their own JSON; a tool-level error is a valid result payload (the bridge only
        // wraps thrown exceptions). This keeps "nothing matched" distinct from a transport failure.
        private static string Error(string message)
            => new JObject { ["error"] = message }.ToString(Formatting.None);

        private static JObject ParseArgs(string argumentsJson)
        {
            try { return JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson); }
            catch { return new JObject(); }
        }

        /// <summary>
        /// Converts a JSON fields object into a fieldName -> <see cref="FieldNode"/> map. Nested JSON
        /// objects become composite nodes and JSON arrays become list nodes, so composite serializable
        /// fields and lists-of-objects (e.g. a <c>DynamicLocalization</c> with per-language
        /// <c>translations</c>) can be authored — not just flat scalars.
        /// </summary>
        private static Dictionary<string, FieldNode> ToFieldNodeMap(JObject fields)
        {
            var map = new Dictionary<string, FieldNode>();
            foreach (var pair in fields)
                map[pair.Key] = ToFieldNode(pair.Value);
            return map;
        }

        /// <summary>Recursively maps a JSON token to a neutral <see cref="FieldNode"/> for coercion.</summary>
        private static FieldNode ToFieldNode(JToken token)
        {
            switch (token)
            {
                case JObject obj:
                    var members = new Dictionary<string, FieldNode>();
                    foreach (var pair in obj)
                        members[pair.Key] = ToFieldNode(pair.Value);
                    return FieldNode.FromMembers(members);

                case JArray arr:
                    // An all-scalar array stays a list of scalar nodes; the coercion layer folds it back
                    // to the comma form for numeric-tuple fields (Vector*/Color/Rect) or writes it
                    // element-wise for real array/list fields. Object elements recurse.
                    return FieldNode.FromList(arr.Select(ToFieldNode).ToList());

                default:
                    return FieldNode.FromScalar(ScalarToString(token));
            }
        }

        /// <summary>Renders a JSON scalar to the string form the coercion layer expects (invariant).</summary>
        private static string ScalarToString(JToken token)
        {
            if (token == null) return null;
            switch (token.Type)
            {
                case JTokenType.String: return token.Value<string>();
                case JTokenType.Boolean: return token.Value<bool>() ? "true" : "false";
                case JTokenType.Null: return "null";
                case JTokenType.Integer:
                    return token.Value<long>().ToString(CultureInfo.InvariantCulture);
                case JTokenType.Float:
                    return token.Value<double>().ToString(CultureInfo.InvariantCulture);
                case JTokenType.Array:
                    // Vectors/colors/rects and array/list fields are authored as JSON arrays; flatten to
                    // the comma-separated token form the coercion layer parses (e.g. [1,2,3] -> "1,2,3").
                    return string.Join(",", ((JArray)token).Select(ScalarToString));
                default:
                    // Objects are not coercible into a single field; pass a form that will be rejected
                    // with a clear field-type error rather than silently dropped.
                    return token.ToString(Formatting.None);
            }
        }

        private static JArray RejectedToJson(IReadOnlyList<KeyValuePair<string, string>> rejected)
        {
            var arr = new JArray();
            foreach (var pair in rejected)
                arr.Add(new JObject { ["field"] = pair.Key, ["reason"] = pair.Value });
            return arr;
        }
    }
}
