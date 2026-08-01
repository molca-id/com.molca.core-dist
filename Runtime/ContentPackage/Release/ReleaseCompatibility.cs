using System;

namespace Molca.ContentPackage.Release
{
    /// <summary>
    /// SemVer 2.0.0 precedence and the release compatibility range — contract §4.4.
    /// </summary>
    /// <remarks>
    /// Written out rather than delegating to <see cref="Version"/>, which is not SemVer.
    /// <c>Version.TryParse</c> rejects <c>2.4.0-beta.1</c> outright and treats <c>1.2</c> and
    /// <c>1.2.0</c> as equal-but-different shapes. A build whose app version carries a prerelease
    /// suffix — every internal build, in practice — would fail to parse, and the caller's usual
    /// response to an unparseable version is to treat everything as compatible. So the one
    /// population most likely to hit an incompatible release is the one whose check silently
    /// switches off.
    ///
    /// Precedence follows the specification: numeric identifiers compare numerically, alphanumeric
    /// ones ordinally, a prerelease sorts below its release, and build metadata is ignored entirely.
    /// </remarks>
    public static class ReleaseCompatibility
    {
        /// <summary>
        /// True when <paramref name="appVersion"/> falls within the inclusive range.
        /// </summary>
        /// <remarks>
        /// An empty bound means unbounded. An <em>unparseable</em> bound is treated as absent, which
        /// widens the range — the alternative, refusing to activate, would strand a fleet on a
        /// server-side typo. An unparseable app version is treated as compatible for the same
        /// reason, and both cases are reported through <paramref name="explanation"/> so the
        /// decision is visible rather than silent.
        /// </remarks>
        /// <param name="appVersion">The running app version.</param>
        /// <param name="minAppVersion">Inclusive lower bound, or empty.</param>
        /// <param name="maxAppVersion">Inclusive upper bound, or empty.</param>
        /// <param name="explanation">Why the answer is what it is; empty when plainly in range.</param>
        /// <returns>True when the release may activate on this app version.</returns>
        public static bool IsInRange(
            string appVersion, string minAppVersion, string maxAppVersion, out string explanation)
        {
            explanation = "";

            if (!TryParse(appVersion, out var app))
            {
                explanation = $"App version '{appVersion}' is not SemVer; the compatibility range was not enforced.";
                return true;
            }

            if (!string.IsNullOrEmpty(minAppVersion))
            {
                if (!TryParse(minAppVersion, out var min))
                    explanation = $"minAppVersion '{minAppVersion}' is not SemVer; treated as no lower bound.";
                else if (Compare(app, min) < 0)
                {
                    explanation = $"App {appVersion} is below the release minimum {minAppVersion}.";
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(maxAppVersion))
            {
                if (!TryParse(maxAppVersion, out var max))
                    explanation += $" maxAppVersion '{maxAppVersion}' is not SemVer; treated as no upper bound.";
                else if (Compare(app, max) > 0)
                {
                    explanation = $"App {appVersion} is above the release maximum {maxAppVersion}.";
                    return false;
                }
            }

            return true;
        }

        /// <summary>Convenience overload that discards the explanation.</summary>
        /// <param name="appVersion">The running app version.</param>
        /// <param name="minAppVersion">Inclusive lower bound, or empty.</param>
        /// <param name="maxAppVersion">Inclusive upper bound, or empty.</param>
        public static bool IsInRange(string appVersion, string minAppVersion, string maxAppVersion) =>
            IsInRange(appVersion, minAppVersion, maxAppVersion, out _);

        /// <summary>A parsed SemVer version. Build metadata is discarded, per precedence rules.</summary>
        public readonly struct SemVer
        {
            /// <summary>Major version.</summary>
            public readonly int Major;

            /// <summary>Minor version.</summary>
            public readonly int Minor;

            /// <summary>Patch version.</summary>
            public readonly int Patch;

            /// <summary>Dot-separated prerelease identifiers, or an empty array for a release.</summary>
            public readonly string[] Prerelease;

            internal SemVer(int major, int minor, int patch, string[] prerelease)
            {
                Major = major; Minor = minor; Patch = patch;
                Prerelease = prerelease ?? Array.Empty<string>();
            }
        }

        /// <summary>Parses a SemVer 2.0.0 string.</summary>
        /// <param name="value">The version text.</param>
        /// <param name="version">The parsed version, when this returns true.</param>
        public static bool TryParse(string value, out SemVer version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(value)) return false;

            string text = value.Trim();

            // Build metadata is ignored for precedence, so it is dropped before anything else.
            int plus = text.IndexOf('+');
            if (plus >= 0) text = text.Substring(0, plus);

            string[] prerelease = Array.Empty<string>();
            int dash = text.IndexOf('-');
            if (dash >= 0)
            {
                string tail = text.Substring(dash + 1);
                text = text.Substring(0, dash);
                if (tail.Length == 0) return false;
                prerelease = tail.Split('.');
                foreach (var identifier in prerelease)
                    if (identifier.Length == 0) return false;
            }

            var parts = text.Split('.');
            if (parts.Length != 3) return false;
            if (!TryParseNumber(parts[0], out int major)) return false;
            if (!TryParseNumber(parts[1], out int minor)) return false;
            if (!TryParseNumber(parts[2], out int patch)) return false;

            version = new SemVer(major, minor, patch, prerelease);
            return true;
        }

        /// <summary>Compares two versions by SemVer precedence.</summary>
        /// <param name="left">Left operand.</param>
        /// <param name="right">Right operand.</param>
        /// <returns>Negative, zero, or positive.</returns>
        public static int Compare(SemVer left, SemVer right)
        {
            if (left.Major != right.Major) return left.Major.CompareTo(right.Major);
            if (left.Minor != right.Minor) return left.Minor.CompareTo(right.Minor);
            if (left.Patch != right.Patch) return left.Patch.CompareTo(right.Patch);

            bool leftPre = left.Prerelease.Length > 0;
            bool rightPre = right.Prerelease.Length > 0;
            if (leftPre && !rightPre) return -1;
            if (!leftPre && rightPre) return 1;
            if (!leftPre) return 0;

            int shared = Math.Min(left.Prerelease.Length, right.Prerelease.Length);
            for (int i = 0; i < shared; i++)
            {
                int comparison = ComparePrereleaseIdentifier(left.Prerelease[i], right.Prerelease[i]);
                if (comparison != 0) return comparison;
            }
            // A longer identifier list has higher precedence when all earlier ones are equal.
            return left.Prerelease.Length.CompareTo(right.Prerelease.Length);
        }

        private static int ComparePrereleaseIdentifier(string left, string right)
        {
            bool leftNumeric = TryParseNumber(left, out int leftValue);
            bool rightNumeric = TryParseNumber(right, out int rightValue);

            if (leftNumeric && rightNumeric) return leftValue.CompareTo(rightValue);
            // Numeric identifiers always have lower precedence than alphanumeric ones.
            if (leftNumeric) return -1;
            if (rightNumeric) return 1;
            return string.CompareOrdinal(left, right);
        }

        private static bool TryParseNumber(string value, out int number)
        {
            number = 0;
            if (string.IsNullOrEmpty(value)) return false;
            // Leading zeros are not valid SemVer numeric identifiers, and "01" vs "1" comparing
            // equal would make two distinct published versions indistinguishable here.
            if (value.Length > 1 && value[0] == '0') return false;
            foreach (char c in value)
                if (c < '0' || c > '9') return false;
            return int.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out number);
        }
    }
}
