using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace Molca.Editor.Upgrade
{
    /// <summary>Finds persistent UnityEvent calls that still target the removed v1 ColorID component.</summary>
    public sealed class RetiredColorUnityEventDetector : IMolcaUpgradeDetector
    {
        internal const string FindingCode = "colorid.retired-unityevent-callbacks";
        private const string RetiredType = "Molca.ColorID.ColorID, Molca";
        private const string RetiredMethod = "SetColorId";

        /// <inheritdoc/>
        public string System => "Colour Theme";

        /// <inheritdoc/>
        public IEnumerable<MolcaUpgradeFinding> Detect()
        {
            var locations = new List<string>();
            foreach (string path in AssetPaths())
            {
                string absolute = Path.GetFullPath(path);
                if (!File.Exists(absolute)) continue;
                locations.AddRange(FindInText(path, File.ReadAllText(absolute)));
            }

            if (locations.Count == 0) yield break;

            yield return new MolcaUpgradeFinding(
                FindingCode,
                $"{locations.Count} UnityEvent callback(s) still use ColorID.SetColorId metadata",
                "The callback method is removed in 2.0, so Unity cannot invoke retired targets. Open each "
                + "location and either remove the obsolete call or recreate its authored behaviour with "
                + "ColorThemeBinding/token-aware UI. This is reported rather than guessed because the "
                + "correct V2 token and interaction are project decisions. An inherited target type is "
                + "labelled for verification when a prefab override stores only the method name.",
                MolcaUpgradeSeverity.Blocking,
                locations.OrderBy(location => location, StringComparer.Ordinal).ToList());
        }

        internal static IReadOnlyList<string> FindInText(string assetPath, string text)
        {
            var lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            var locations = new List<string>();

            // Source prefab/scene calls serialize the target type and method in one compact call block.
            for (int i = 0; i < lines.Length; i++)
            {
                if (!IsField(lines[i], "m_MethodName", RetiredMethod)) continue;

                bool retiredTarget = false;
                for (int index = i - 1; index >= 0; index--)
                {
                    string previous = lines[index].Trim();
                    if (IsField(lines[index], "m_TargetAssemblyTypeName", RetiredType))
                    {
                        retiredTarget = true;
                        break;
                    }
                    if (previous.StartsWith("- m_Target:", StringComparison.Ordinal) ||
                        previous.StartsWith("m_MethodName:", StringComparison.Ordinal))
                        break;
                }

                if (retiredTarget)
                {
                    string argument = FindForwardField(lines, i + 1, "m_StringArgument");
                    string context = string.IsNullOrEmpty(argument)
                        ? $"persistent call to {RetiredMethod}"
                        : $"persistent call to {RetiredMethod}('{argument}')";
                    locations.Add($"{assetPath}:{i + 1} — {context}");
                }
            }

            // Prefab instances serialize changed call members as independent property overrides. Pair
            // target and method by their shared Array.data[n] path, not merely by sharing a file.
            var overrides = ReadOverrides(lines);
            foreach (var pair in overrides)
            {
                if (!pair.Key.Path.EndsWith(".m_MethodName", StringComparison.Ordinal) ||
                    !string.Equals(pair.Value.Value, RetiredMethod, StringComparison.Ordinal))
                    continue;

                string prefix = pair.Key.Path.Substring(0, pair.Key.Path.Length - ".m_MethodName".Length);
                if (overrides.TryGetValue(
                        (pair.Key.Document, pair.Key.Target, prefix + ".m_TargetAssemblyTypeName"),
                        out var target))
                {
                    if (!string.Equals(target.Value, RetiredType, StringComparison.Ordinal)) continue;

                    overrides.TryGetValue(
                        (pair.Key.Document, pair.Key.Target, prefix + ".m_Arguments.m_StringArgument"),
                        out var argument);
                    string suffix = string.IsNullOrEmpty(argument.Value)
                        ? prefix
                        : $"{prefix} = '{argument.Value}'";
                    locations.Add($"{assetPath}:{pair.Value.Line} — {suffix}");
                }
                else
                {
                    // A prefab instance need serialize only the changed method; its target type may be
                    // inherited from the source prefab. SetColorId is retired metadata worth reviewing,
                    // but label the uncertainty rather than asserting the target type was proven.
                    locations.Add($"{assetPath}:{pair.Value.Line} — {prefix} "
                                  + "(SetColorId; target type inherited — verify)");
                }
            }

            return locations.Distinct().ToList();
        }

        private static Dictionary<(string Document, string Target, string Path), (string Value, int Line)>
            ReadOverrides(string[] lines)
        {
            var result = new Dictionary<
                (string Document, string Target, string Path), (string Value, int Line)>();
            string document = string.Empty;
            for (int i = 0; i + 1 < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith("--- !u!", StringComparison.Ordinal))
                {
                    document = trimmed;
                    continue;
                }
                if (!trimmed.StartsWith("propertyPath:", StringComparison.Ordinal)) continue;

                string path = trimmed.Substring("propertyPath:".Length).Trim();
                string next = lines[i + 1].Trim();
                if (!next.StartsWith("value:", StringComparison.Ordinal)) continue;

                string target = i > 0 && lines[i - 1].Trim().StartsWith("- target:", StringComparison.Ordinal)
                    ? lines[i - 1].Trim()
                    : string.Empty;
                result[(document, target, path)] = (next.Substring("value:".Length).Trim(), i + 2);
            }
            return result;
        }

        private static string FindForwardField(string[] lines, int first, string field)
        {
            for (int i = first; i < Math.Min(lines.Length, first + 16); i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith("- m_Target:", StringComparison.Ordinal)) break;
                string prefix = field + ":";
                if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                    return trimmed.Substring(prefix.Length).Trim();
            }
            return string.Empty;
        }

        private static bool IsField(string line, string field, string expected)
        {
            string trimmed = (line ?? string.Empty).Trim();
            string prefix = field + ":";
            return trimmed.StartsWith(prefix, StringComparison.Ordinal) &&
                   string.Equals(trimmed.Substring(prefix.Length).Trim(), expected, StringComparison.Ordinal);
        }

        private static IEnumerable<string> AssetPaths()
        {
            return AssetDatabase.GetAllAssetPaths()
                .Where(path => path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                .Where(path => path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .OrderBy(path => path, StringComparer.Ordinal);
        }
    }
}
