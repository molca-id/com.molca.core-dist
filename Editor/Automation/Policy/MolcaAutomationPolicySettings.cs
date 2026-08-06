using System.Collections.Generic;
using UnityEngine;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// The named policy profile in force for automation runs (§9.3). Profiles move from most restrictive
    /// (observe) to most capable (CI), trading interactivity for an exact allowlist.
    /// </summary>
    public enum MolcaAutomationProfile
    {
        /// <summary>Read-only commands only; every action is refused.</summary>
        Observe,

        /// <summary>Allowlisted undoable/snapshot actions run; irreversible actions require confirmation.</summary>
        Develop,

        /// <summary>Curated validation/build/deploy actions with explicit environment constraints.</summary>
        Release,

        /// <summary>Exact command allowlist, no interactive confirmation; credentials from the environment.</summary>
        UnattendedCi
    }

    /// <summary>
    /// Authored automation policy: the active <see cref="MolcaAutomationProfile"/> and the set of action
    /// command ids permitted to run (§9.3). This is the new home for policy choices; during migration the
    /// effective allowlist is the <em>union</em> of these entries and the legacy
    /// <see cref="Molca.Editor.Mcp.McpSettings.ActionToolAllowlist"/> (read-only compat), but new choices
    /// are written only here. Config only — never store credentials on this asset (§15).
    /// <para>
    /// <b>The profile is per machine; the allowlist is not.</b> <see cref="ActiveProfile"/> reads through
    /// <see cref="MolcaLocalSettings"/> and its setter writes only there, because "this box is a CI runner" is
    /// a property of the box rather than of the project — and a committed profile means every clone inherits
    /// whatever the last commit happened to carry. <see cref="ActionAllowlist"/> stays on the asset: which
    /// commands may <i>ever</i> run is a project decision, and widening it should show up in a diff.
    /// </para>
    /// </summary>
    [Icon("Packages/com.molca.core/Editor/Icons/molca-mcp.png")]
    public class MolcaAutomationPolicySettings : ScriptableObject
    {
        [Tooltip("Project default policy profile for automation runs. Each machine can override this locally "
               + "without committing the change — a CI runner is configured where it runs, not in the repo.")]
        [SerializeField] private MolcaAutomationProfile activeProfile = MolcaAutomationProfile.Observe;

        [Tooltip("Command ids of Action (mutating) commands permitted to run under Develop/Release/CI.")]
        [SerializeField] private List<string> actionAllowlist = new List<string>();

        /// <summary>
        /// The policy profile in force on this machine — the local override if set, otherwise
        /// <see cref="ProjectDefaultActiveProfile"/>. The setter writes the machine-local overlay, never the asset.
        /// </summary>
        public MolcaAutomationProfile ActiveProfile
        {
            get => MolcaLocalOverlay.GetEnum(
                this, MolcaLocalSettings.Keys.AutomationActiveProfile, activeProfile);
            set => MolcaLocalOverlay.SetEnum(
                this, MolcaLocalSettings.Keys.AutomationActiveProfile, ref activeProfile, value);
        }

        /// <summary>
        /// The committed project default for <see cref="ActiveProfile"/>, ignoring any local override.
        /// </summary>
        public MolcaAutomationProfile ProjectDefaultActiveProfile => activeProfile;

        /// <summary>Command ids of actions this asset permits. Never null.</summary>
        public IReadOnlyList<string> ActionAllowlist
            => actionAllowlist ?? (IReadOnlyList<string>)System.Array.Empty<string>();

        /// <summary>Sets the profile in force on this machine, persisting it to the local overlay.</summary>
        /// <param name="profile">The profile to activate.</param>
        /// <remarks>
        /// Writes <see cref="MolcaLocalSettings"/>, not the asset — see the type remarks. To change what every
        /// clone gets by default, edit the asset's authored value instead.
        /// </remarks>
        public void SetActiveProfile(MolcaAutomationProfile profile) => ActiveProfile = profile;

        /// <summary>Drops this machine's profile override, so the committed project default applies again.</summary>
        public void ClearActiveProfileOverride()
            => ClearLocalOverride(MolcaLocalSettings.Keys.AutomationActiveProfile);

        /// <summary>True when this machine overrides the project default for <paramref name="key"/>.</summary>
        /// <param name="key">A key from <see cref="MolcaLocalSettings.Keys"/> belonging to this asset.</param>
        public bool HasLocalOverride(string key) => MolcaLocalOverlay.IsOverridden(this, key);

        /// <summary>Drops this machine's override for <paramref name="key"/>, restoring the project default.</summary>
        /// <param name="key">A key from <see cref="MolcaLocalSettings.Keys"/> belonging to this asset.</param>
        public void ClearLocalOverride(string key) => MolcaLocalSettings.Instance.Clear(key);

        /// <summary>True when this machine overrides the committed <see cref="ProjectDefaultActiveProfile"/>.</summary>
        public bool HasActiveProfileOverride
            => MolcaLocalOverlay.IsOverridden(this, MolcaLocalSettings.Keys.AutomationActiveProfile);

        /// <summary>Adds or removes an action command id from the allowlist and persists.</summary>
        /// <param name="commandId">The action command id.</param>
        /// <param name="allowed">True to allow, false to remove.</param>
        public void SetActionAllowed(string commandId, bool allowed)
        {
            if (string.IsNullOrEmpty(commandId)) return;
            actionAllowlist ??= new List<string>();
            if (allowed)
            {
                if (!actionAllowlist.Contains(commandId)) actionAllowlist.Add(commandId);
            }
            else
            {
                actionAllowlist.Remove(commandId);
            }
            Persist();
        }

        /// <summary>Adds or removes many action command ids in one persisted change (bulk authoring).</summary>
        /// <param name="commandIds">The action command ids.</param>
        /// <param name="allowed">True to allow all of them, false to remove all of them.</param>
        public void SetActionsAllowed(System.Collections.Generic.IEnumerable<string> commandIds, bool allowed)
        {
            if (commandIds == null) return;
            actionAllowlist ??= new List<string>();
            foreach (var id in commandIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (allowed) { if (!actionAllowlist.Contains(id)) actionAllowlist.Add(id); }
                else actionAllowlist.Remove(id);
            }
            Persist();
        }

        /// <summary>Clears the entire action allowlist (keeps the active profile).</summary>
        public void ClearAllowlist()
        {
            actionAllowlist?.Clear();
            Persist();
        }

        /// <summary>Resets to safe defaults: the Observe profile with an empty allowlist.</summary>
        /// <remarks>
        /// Drops this machine's profile override <em>and</em> re-authors the project default, so the effective
        /// profile is Observe either way — a reset that left a local override in place would not be a reset.
        /// </remarks>
        public void ResetToDefaults()
        {
            ClearActiveProfileOverride();
            activeProfile = MolcaAutomationProfile.Observe;
            actionAllowlist?.Clear();
            Persist();
        }

        private void Persist()
        {
            // A test-injected instance is not an asset; SetDirty/SaveAssetIfDirty on it is meaningless and
            // would log. In-memory state is all a test needs.
            if (!UnityEditor.EditorUtility.IsPersistent(this)) return;
            UnityEditor.EditorUtility.SetDirty(this);
            UnityEditor.AssetDatabase.SaveAssetIfDirty(this);
        }

        private static MolcaAutomationPolicySettings _overrideForTests;

        /// <summary>
        /// Substitutes an in-memory instance for the project's policy asset, so a test can exercise
        /// authorization without creating or mutating <c>Assets/_Molca/Editor/Automation Policy.asset</c>.
        /// Pass <c>null</c> to restore the real asset.
        /// </summary>
        /// <remarks>
        /// The counterpart of <c>AssistantMemoryStore.OverrideRootForTests</c> for policy: this asset is the
        /// project's standing authorization, so a test writing to it would silently change what the
        /// developer's assistant is permitted to run.
        /// </remarks>
        /// <param name="instance">A <c>ScriptableObject.CreateInstance</c>d settings object, or null.</param>
        public static void OverrideForTests(MolcaAutomationPolicySettings instance) => _overrideForTests = instance;

        /// <summary>Loads the existing policy asset, creating one at the default path if none exists.</summary>
        /// <returns>The shared <see cref="MolcaAutomationPolicySettings"/> asset.</returns>
        public static MolcaAutomationPolicySettings GetOrCreateSettings()
            => _overrideForTests
               ?? Molca.Editor.MolcaEditorSettingsAsset.GetOrCreate<MolcaAutomationPolicySettings>(
                   "Automation Policy.asset");
    }
}
