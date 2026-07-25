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
    /// </summary>
    [Icon("Packages/com.molca.core/Editor/Icons/molca-mcp.png")]
    public class MolcaAutomationPolicySettings : ScriptableObject
    {
        [Tooltip("The active policy profile in force for automation runs.")]
        [SerializeField] private MolcaAutomationProfile activeProfile = MolcaAutomationProfile.Observe;

        [Tooltip("Command ids of Action (mutating) commands permitted to run under Develop/Release/CI.")]
        [SerializeField] private List<string> actionAllowlist = new List<string>();

        /// <summary>The active policy profile.</summary>
        public MolcaAutomationProfile ActiveProfile
        {
            get => activeProfile;
            set => activeProfile = value;
        }

        /// <summary>Command ids of actions this asset permits. Never null.</summary>
        public IReadOnlyList<string> ActionAllowlist
            => actionAllowlist ?? (IReadOnlyList<string>)System.Array.Empty<string>();

        /// <summary>Sets the active profile and persists the asset.</summary>
        /// <param name="profile">The profile to activate.</param>
        public void SetActiveProfile(MolcaAutomationProfile profile)
        {
            activeProfile = profile;
            Persist();
        }

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
        public void ResetToDefaults()
        {
            activeProfile = MolcaAutomationProfile.Observe;
            actionAllowlist?.Clear();
            Persist();
        }

        private void Persist()
        {
            UnityEditor.EditorUtility.SetDirty(this);
            UnityEditor.AssetDatabase.SaveAssetIfDirty(this);
        }

        /// <summary>Loads the existing policy asset, creating one at the default path if none exists.</summary>
        /// <returns>The shared <see cref="MolcaAutomationPolicySettings"/> asset.</returns>
        public static MolcaAutomationPolicySettings GetOrCreateSettings()
            => Molca.Editor.MolcaEditorSettingsAsset.GetOrCreate<MolcaAutomationPolicySettings>(
                "Automation Policy.asset");
    }
}
