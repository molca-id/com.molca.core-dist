using System;

namespace Molca.Editor.Addons
{
    /// <summary>Small SemVer comparator/range evaluator matching the add-on distribution v1 contract.</summary>
    internal static class AddonSemVer
    {
        internal readonly struct Version : IComparable<Version>
        {
            public Version(int major, int minor, int patch, string prerelease)
            {
                Major = major;
                Minor = minor;
                Patch = patch;
                Prerelease = prerelease ?? string.Empty;
            }

            public int Major { get; }
            public int Minor { get; }
            public int Patch { get; }
            public string Prerelease { get; }

            public int CompareTo(Version other)
            {
                int value = Major.CompareTo(other.Major);
                if (value != 0) return value;
                value = Minor.CompareTo(other.Minor);
                if (value != 0) return value;
                value = Patch.CompareTo(other.Patch);
                if (value != 0) return value;
                if (Prerelease == other.Prerelease) return 0;
                if (Prerelease.Length == 0) return 1;
                if (other.Prerelease.Length == 0) return -1;
                return StringComparer.OrdinalIgnoreCase.Compare(Prerelease, other.Prerelease);
            }
        }

        internal static bool TryParse(string text, out Version version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string value = text.Trim();
            int plus = value.IndexOf('+');
            if (plus >= 0) value = value.Substring(0, plus);
            string prerelease = string.Empty;
            int dash = value.IndexOf('-');
            if (dash >= 0)
            {
                prerelease = value.Substring(dash + 1);
                value = value.Substring(0, dash);
                if (prerelease.Length == 0) return false;
            }
            string[] parts = value.Split('.');
            if (parts.Length != 3 || !int.TryParse(parts[0], out int major) ||
                !int.TryParse(parts[1], out int minor) || !int.TryParse(parts[2], out int patch) ||
                major < 0 || minor < 0 || patch < 0)
                return false;
            version = new Version(major, minor, patch, prerelease);
            return true;
        }

        /// <summary>
        /// Orders two version strings. Unparseable text sorts as equal so a malformed catalog entry
        /// degrades a label rather than throwing inside a UI rebuild.
        /// </summary>
        /// <returns>Negative when <paramref name="left"/> is older, positive when newer, 0 otherwise.</returns>
        internal static int Compare(string left, string right) =>
            TryParse(left, out var a) && TryParse(right, out var b) ? a.CompareTo(b) : 0;

        internal static bool Satisfies(string versionText, string rangeText)
        {
            if (!TryParse(versionText, out var version)) return false;
            if (string.IsNullOrWhiteSpace(rangeText) || rangeText.Trim() == "*") return true;
            foreach (string raw in rangeText.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string op = "=";
                string targetText = raw;
                foreach (string candidate in new[] { ">=", "<=", ">", "<", "=" })
                {
                    if (!raw.StartsWith(candidate, StringComparison.Ordinal)) continue;
                    op = candidate;
                    targetText = raw.Substring(candidate.Length);
                    break;
                }
                if (!TryParse(targetText, out var target)) return false;
                int comparison = version.CompareTo(target);
                bool matched = op switch
                {
                    ">=" => comparison >= 0,
                    "<=" => comparison <= 0,
                    ">" => comparison > 0,
                    "<" => comparison < 0,
                    _ => comparison == 0,
                };
                if (!matched) return false;
            }
            return true;
        }
    }
}
