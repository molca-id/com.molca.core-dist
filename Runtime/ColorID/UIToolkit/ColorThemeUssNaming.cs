using System.Text;

namespace Molca.ColorID
{
    /// <summary>
    /// Turns canonical token IDs into USS custom-property names, deterministically.
    /// </summary>
    /// <remarks>
    /// Shared by the editor generator and the runtime binder so the two cannot disagree about what a
    /// token is called in USS. A mismatch here would present as a UI Toolkit control silently keeping
    /// its fallback colour, which is far harder to diagnose than a missing variable.
    /// <para/>
    /// The transformation is total and reversible in shape: canonical IDs are already lower-case
    /// alphanumeric with <c>-</c> and <c>/</c>, so replacing <c>/</c> with <c>-</c> and prefixing is
    /// enough — no escaping, no case folding, no collision handling beyond what
    /// <see cref="ColorThemeSet"/> validation already guarantees.
    /// </remarks>
    public static class ColorThemeUssNaming
    {
        /// <summary>Prefix on every generated custom property.</summary>
        public const string VariablePrefix = "--molca-color-";

        /// <summary>USS class the generated variables are declared on.</summary>
        /// <remarks>
        /// Variables are scoped to a class rather than <c>:root</c> so a document can host more than one
        /// themed subtree, and so adding the sheet cannot leak colours into unrelated panels.
        /// </remarks>
        public const string ThemeClass = "molca-theme";

        /// <summary>The USS custom-property name for a canonical token ID.</summary>
        /// <param name="tokenId">A canonical token ID such as <c>text/primary</c>.</param>
        /// <returns>
        /// The variable name, for example <c>--molca-color-text-primary</c>, or <c>null</c> when
        /// <paramref name="tokenId"/> is blank.
        /// </returns>
        public static string ToVariableName(string tokenId)
        {
            if (string.IsNullOrEmpty(tokenId)) return null;

            var builder = new StringBuilder(VariablePrefix.Length + tokenId.Length);
            builder.Append(VariablePrefix);
            foreach (char c in tokenId)
            {
                builder.Append(c == ColorTokenId.Separator ? '-' : c);
            }
            return builder.ToString();
        }
    }
}
