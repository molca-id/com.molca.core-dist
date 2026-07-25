using System;
using System.Globalization;
using Molca.Editor.Addons;

namespace Molca.Editor.About
{
    /// <summary>
    /// Per-project developer preferences and last-known result for the framework update check.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/About/</c>. Backed by <see cref="MolcaEditorPrefs"/>
    /// (project-scoped) rather than a settings asset, matching <see cref="AddonChannels.Preferred"/>: whether
    /// a developer wants an update nag is their choice on their machine, not project configuration to commit.
    /// The last result is persisted so the activity chip can render immediately after a domain reload without
    /// dialing the network. Editor-only.
    /// </remarks>
    internal static class FrameworkUpdatePreferences
    {
        private const string CheckOnOpenKey = "Molca.About.CheckOnOpen";
        private const string ShowChipKey = "Molca.About.ShowActivityChip";
        private const string LastCheckedKey = "Molca.About.LastCheckedUtc";
        private const string LastLatestKey = "Molca.About.LastLatestVersion";
        private const string LastChannelKey = "Molca.About.LastChannel";

        /// <summary>
        /// Whether opening the About section may check the feed when the cached answer is stale. On by
        /// default; the check is still never automatic anywhere else in the editor.
        /// </summary>
        internal static bool CheckOnOpen
        {
            get => MolcaEditorPrefs.GetBool(CheckOnOpenKey, true);
            set => MolcaEditorPrefs.SetBool(CheckOnOpenKey, value);
        }

        /// <summary>
        /// Whether an available update also appears as a chip in the Hub's activity rail. Off by default —
        /// a persistent nag has to be asked for.
        /// </summary>
        internal static bool ShowActivityChip
        {
            get => MolcaEditorPrefs.GetBool(ShowChipKey, false);
            set => MolcaEditorPrefs.SetBool(ShowChipKey, value);
        }

        /// <summary>When the feed was last read successfully, or <c>null</c> if never.</summary>
        internal static DateTime? LastCheckedUtc
        {
            get
            {
                string raw = MolcaEditorPrefs.GetString(LastCheckedKey, string.Empty);
                return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed
                    : (DateTime?)null;
            }
        }

        /// <summary>The newest version the last successful check reported, or empty.</summary>
        internal static string LastLatestVersion => MolcaEditorPrefs.GetString(LastLatestKey, string.Empty);

        /// <summary>The channel the last successful check was served on, or empty.</summary>
        internal static string LastChannel => MolcaEditorPrefs.GetString(LastChannelKey, string.Empty);

        /// <summary>Records the outcome of a successful check so other surfaces can read it offline.</summary>
        /// <param name="latestVersion">Newest version the feed reported; may be empty.</param>
        /// <param name="channel">Channel the feed served.</param>
        /// <param name="checkedUtc">Completion time of the check, UTC.</param>
        internal static void RecordCheck(string latestVersion, string channel, DateTime checkedUtc)
        {
            MolcaEditorPrefs.SetString(LastLatestKey, latestVersion ?? string.Empty);
            MolcaEditorPrefs.SetString(LastChannelKey, channel ?? string.Empty);
            MolcaEditorPrefs.SetString(LastCheckedKey, checkedUtc.ToString("O", CultureInfo.InvariantCulture));
        }
    }
}
