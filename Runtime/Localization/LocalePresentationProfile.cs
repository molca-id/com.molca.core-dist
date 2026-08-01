using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Molca.Localization
{
    /// <summary>Authored writing direction for one locale presentation profile.</summary>
    public enum LocalizationWritingDirection
    {
        Unspecified = 0,
        LeftToRight = 1,
        RightToLeft = 2,
    }

    /// <summary>Locale-specific font, glyph, direction, and line-breaking policy.</summary>
    [CreateAssetMenu(
        fileName = "Locale Presentation Profile",
        menuName = "Molca/Localization/Locale Presentation Profile",
        order = 42)]
    public sealed class LocalePresentationProfile : ScriptableObject
    {
        [SerializeField] private TMP_FontAsset primaryFont;
        [SerializeField] private List<TMP_FontAsset> fallbackFonts = new();
        [SerializeField] private LocalizationWritingDirection writingDirection;
        [SerializeField, TextArea(2, 8)] private string requiredCharacters;
        [SerializeField, TextArea(2, 5)] private string lineBreakingNotes;

        public TMP_FontAsset PrimaryFont => primaryFont;
        public IReadOnlyList<TMP_FontAsset> FallbackFonts => fallbackFonts;
        public LocalizationWritingDirection WritingDirection => writingDirection;
        public string RequiredCharacters => requiredCharacters ?? string.Empty;
        public string LineBreakingNotes => lineBreakingNotes ?? string.Empty;
        public bool IsRightToLeft =>
            writingDirection == LocalizationWritingDirection.RightToLeft;

        /// <summary>Returns the profile font, falling back to the component style font.</summary>
        public TMP_FontAsset ResolvePrimaryFont(TMP_FontAsset styleFont) =>
            primaryFont != null ? primaryFont : styleFont;

        /// <summary>Returns required characters absent from the declared font chain.</summary>
        public IReadOnlyList<char> GetMissingRequiredCharacters()
        {
            var fonts = Enumerable.Repeat(primaryFont, 1)
                .Concat(fallbackFonts ?? new List<TMP_FontAsset>())
                .Where(font => font != null)
                .Distinct()
                .ToArray();
            if (fonts.Length == 0)
                return RequiredCharacters
                    .Where(character => !char.IsWhiteSpace(character))
                    .Distinct()
                    .ToArray();

            return RequiredCharacters
                .Where(character => !char.IsWhiteSpace(character) &&
                                    !fonts.Any(font =>
                                        font.HasCharacter(character, true, false)))
                .Distinct()
                .ToArray();
        }
    }
}
