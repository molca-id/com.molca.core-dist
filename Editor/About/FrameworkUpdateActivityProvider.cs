using System.Collections.Generic;
using Molca.Editor.Hub;

namespace Molca.Editor.About
{
    /// <summary>
    /// Surfaces an available Core update as a single chip in the Hub's bottom activity rail, through the
    /// standard <see cref="MolcaHubActivityProvider"/> seam.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/About/</c>.
    /// Opt-in: nothing is shown unless the developer turned
    /// <see cref="FrameworkUpdatePreferences.ShowActivityChip"/> on in About. A framework that nags about its
    /// own version by default is a framework people learn to ignore.
    /// This provider is a pure reader — it never triggers a check. It renders the last recorded result
    /// (persisted in <see cref="FrameworkUpdatePreferences"/>), so it survives a domain reload without a
    /// network call and shows nothing at all until About has checked once.
    /// The chip is dismissible for the session; dismissal is intentionally not persisted, because the next
    /// editor session re-earns the right to mention an upgrade the project still has not taken.
    /// </remarks>
    internal sealed class FrameworkUpdateActivityProvider : MolcaHubActivityProvider
    {
        private const string ChipId = "framework-update";

        private string _dismissedVersion = string.Empty;

        /// <inheritdoc/>
        public override IEnumerable<MolcaHubActivity> GetActivities()
        {
            if (!FrameworkUpdatePreferences.ShowActivityChip) yield break;

            string latest = FrameworkUpdatePreferences.LastLatestVersion;
            string installed = FrameworkVersionInfo.CoreVersion;
            if (string.IsNullOrEmpty(latest) || string.IsNullOrEmpty(installed)) yield break;
            if (Addons.AddonSemVer.Compare(latest, installed) <= 0) yield break;
            if (_dismissedVersion == latest) yield break;

            yield return new MolcaHubActivity(
                id: ChipId,
                label: "Molca",
                status: $"Core {latest} available · you have {installed}",
                state: MolcaHubActivityState.Warning,
                onClick: MolcaHubWindow.OpenAbout,
                onDismiss: () =>
                {
                    _dismissedVersion = latest;
                    NotifyChanged();
                },
                order: 100);
        }
    }
}
