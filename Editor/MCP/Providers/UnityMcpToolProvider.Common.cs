using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Shared helpers for Unity MCP tool-family partials.
    /// </summary>
    public sealed partial class UnityMcpToolProvider
    {
        private static JObject ParseArgs(string argumentsJson)
            => JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);

        private static string Error(string message)
            => new JObject { ["error"] = message ?? "Unknown error." }.ToString(Formatting.None);

        private static Vector3? ReadVector3(JToken token)
        {
            if (token is JArray array && array.Count == 3)
                return new Vector3((float)array[0], (float)array[1], (float)array[2]);
            return null;
        }

        private static JArray Vector3ToJson(Vector3 v) => new JArray { v.x, v.y, v.z };
    }
}
