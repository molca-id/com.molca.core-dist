namespace Molca.ColorID
{
    /// <summary>
    /// Grammar and normalization rules for canonical colour-token identifiers.
    /// </summary>
    /// <remarks>
    /// The canonical form is lower-case, slash-separated ASCII with at least two segments:
    /// <code>[a-z0-9][a-z0-9-]*(/[a-z0-9][a-z0-9-]*)+</code>
    /// for example <c>surface/canvas</c>, <c>action/primary/on-fill</c>,
    /// <c>palette/neutral/900</c>.
    /// <para/>
    /// IDs are <b>identity, not presentation</b>. Renaming one is a migration with a preview and an
    /// optional alias, never ordinary text editing — see the transaction rules in the revamp plan.
    /// Requiring at least one slash is deliberate: it makes a canonical token ID structurally
    /// impossible to confuse with a legacy bare colour ID such as <c>Primary</c>.
    /// <para/>
    /// Validation is hand-rolled rather than regex-based so it allocates nothing and can be called
    /// freely during activation of a large token set.
    /// </remarks>
    public static class ColorTokenId
    {
        /// <summary>The separator between canonical ID segments.</summary>
        public const char Separator = '/';

        /// <summary>Maximum length accepted for a canonical token ID.</summary>
        /// <remarks>
        /// A bound exists so that generated artifacts (USS variable names, persistence keys) have a
        /// predictable ceiling. It is far above any sensible semantic name.
        /// </remarks>
        public const int MaxLength = 200;

        /// <summary>
        /// Whether <paramref name="tokenId"/> is a well-formed canonical colour-token identifier.
        /// </summary>
        /// <param name="tokenId">The identifier to check. <c>null</c> and empty are invalid.</param>
        /// <returns><c>true</c> when the value matches the canonical grammar exactly.</returns>
        public static bool IsValid(string tokenId) => Validate(tokenId, out _);

        /// <summary>
        /// Validates <paramref name="tokenId"/> and explains the first rule it breaks.
        /// </summary>
        /// <param name="tokenId">The identifier to check.</param>
        /// <param name="error">
        /// A human-readable reason the value is invalid, or <c>null</c> when it is valid. Written for
        /// an author reading a validation report, not for a developer reading a stack trace.
        /// </param>
        /// <returns><c>true</c> when the value matches the canonical grammar exactly.</returns>
        public static bool Validate(string tokenId, out string error)
        {
            if (string.IsNullOrEmpty(tokenId))
            {
                error = "Token ID is empty.";
                return false;
            }

            if (tokenId.Length > MaxLength)
            {
                error = $"Token ID is {tokenId.Length} characters; the maximum is {MaxLength}.";
                return false;
            }

            int segmentCount = 0;
            int segmentLength = 0;

            for (int i = 0; i < tokenId.Length; i++)
            {
                char c = tokenId[i];

                if (c == Separator)
                {
                    if (segmentLength == 0)
                    {
                        error = i == 0
                            ? "Token ID starts with '/'."
                            : "Token ID contains an empty segment ('//').";
                        return false;
                    }

                    segmentCount++;
                    segmentLength = 0;
                    continue;
                }

                bool isLowerAlphaNumeric = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');

                // A segment may not open with '-', so the first character is checked more strictly
                // than the rest. This keeps generated identifiers (USS custom properties, C#
                // constants) derivable without special-casing leading punctuation.
                if (segmentLength == 0)
                {
                    if (!isLowerAlphaNumeric)
                    {
                        error = c >= 'A' && c <= 'Z'
                            ? $"Token ID contains an upper-case character '{c}'; canonical IDs are lower-case."
                            : $"Segment starts with '{c}'; segments must start with a lower-case letter or digit.";
                        return false;
                    }
                }
                else if (!isLowerAlphaNumeric && c != '-')
                {
                    error = c >= 'A' && c <= 'Z'
                        ? $"Token ID contains an upper-case character '{c}'; canonical IDs are lower-case."
                        : $"Token ID contains the unsupported character '{c}'.";
                    return false;
                }

                segmentLength++;
            }

            if (segmentLength == 0)
            {
                error = "Token ID ends with '/'.";
                return false;
            }

            segmentCount++;

            if (segmentCount < 2)
            {
                error = $"Token ID '{tokenId}' has only one segment; canonical IDs need at least two "
                        + "(for example 'text/primary'), which is what distinguishes them from legacy bare colour IDs.";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Converts an authored value to canonical form where that is unambiguous.
        /// </summary>
        /// <param name="value">The raw authored value.</param>
        /// <returns>
        /// The normalized ID, or <c>null</c> when <paramref name="value"/> cannot be normalized.
        /// </returns>
        /// <remarks>
        /// Only whitespace trimming, case folding and separator unification (a legacy <c>.</c> or a
        /// Windows-style <c>\</c> becomes <c>/</c>) are applied. This deliberately does <b>not</b>
        /// invent segments or rewrite words: normalizing <c>Default.Primary</c> to
        /// <c>default/primary</c> is a mechanical spelling change, whereas mapping it to
        /// <c>action/primary/fill</c> is a semantic decision that belongs in a reviewed migration
        /// alias, never in a string helper.
        /// </remarks>
        public static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var chars = value.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (c == '.' || c == '\\') chars[i] = Separator;
                else if (c >= 'A' && c <= 'Z') chars[i] = (char)(c + ('a' - 'A'));
                else if (c == ' ' || c == '_') chars[i] = '-';
            }

            string normalized = new string(chars);
            return IsValid(normalized) ? normalized : null;
        }
    }
}
