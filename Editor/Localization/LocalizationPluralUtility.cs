using System;
using System.Collections.Generic;

namespace Molca.Editor
{
    /// <summary>Extracts stable plural selector/branch signatures from Unity Smart Strings.</summary>
    public static class LocalizationPluralUtility
    {
        public static HashSet<string> Extract(string value)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(value))
                return result;

            const string marker = ":plural:";
            var searchFrom = 0;
            while (searchFrom < value.Length)
            {
                var markerIndex = value.IndexOf(marker, searchFrom, StringComparison.Ordinal);
                if (markerIndex < 0)
                    break;
                var opening = value.LastIndexOf('{', markerIndex);
                if (opening < 0)
                {
                    searchFrom = markerIndex + marker.Length;
                    continue;
                }

                var depth = 1;
                var branchCount = 1;
                var closing = -1;
                for (var index = markerIndex + marker.Length; index < value.Length; index++)
                {
                    if (value[index] == '{')
                        depth++;
                    else if (value[index] == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            closing = index;
                            break;
                        }
                    }
                    else if (value[index] == '|' && depth == 1)
                        branchCount++;
                }

                if (closing < 0)
                {
                    searchFrom = markerIndex + marker.Length;
                    continue;
                }

                var selector = value.Substring(
                    opening + 1,
                    markerIndex - opening - 1).Trim();
                result.Add($"{selector}:{branchCount}");
                searchFrom = closing + 1;
            }
            return result;
        }

        public static bool ContainsMalformedPlural(string value) =>
            !string.IsNullOrEmpty(value) &&
            value.IndexOf(":plural:", StringComparison.Ordinal) >= 0 &&
            Extract(value).Count == 0;
    }
}
