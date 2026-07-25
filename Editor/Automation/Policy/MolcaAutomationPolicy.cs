using System;
using System.Collections.Generic;
using Molca.Editor.Mcp;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// The profile-based authorization policy (§9.3, §13). Read-only commands are always allowed;
    /// actions are gated by the active <see cref="MolcaAutomationProfile"/> and an allowlist that, during
    /// migration, is the <em>union</em> of the new <see cref="MolcaAutomationPolicySettings"/> entries and
    /// the legacy <see cref="McpSettings.ActionToolAllowlist"/>. Pure and deterministic over its inputs so
    /// it is directly unit-testable.
    /// </summary>
    public sealed class MolcaAutomationPolicy : IMolcaAutomationPolicy
    {
        private readonly MolcaAutomationProfile _profile;
        private readonly HashSet<string> _actionAllowlist;

        /// <summary>Creates a policy over an explicit profile and action allowlist (used by tests).</summary>
        /// <param name="profile">The active profile.</param>
        /// <param name="actionAllowlist">Permitted action command ids (null → none).</param>
        public MolcaAutomationPolicy(MolcaAutomationProfile profile, IEnumerable<string> actionAllowlist)
        {
            _profile = profile;
            _actionAllowlist = new HashSet<string>(actionAllowlist ?? Array.Empty<string>(), StringComparer.Ordinal);
        }

        /// <summary>The active profile this policy enforces.</summary>
        public MolcaAutomationProfile Profile => _profile;

        /// <summary>
        /// Builds a policy from the project's automation settings, taking the union of the new allowlist
        /// and the legacy MCP allowlist so adapted MCP actions keep working during migration (§9.3).
        /// </summary>
        /// <returns>A policy reflecting the current settings.</returns>
        public static MolcaAutomationPolicy FromSettings()
        {
            var settings = MolcaAutomationPolicySettings.GetOrCreateSettings();
            var union = new HashSet<string>(settings.ActionAllowlist, StringComparer.Ordinal);
            union.UnionWith(McpSettings.GetOrCreateSettings().ActionToolAllowlist);
            return new MolcaAutomationPolicy(settings.ActiveProfile, union);
        }

        /// <summary>Whether the effective (union) allowlist permits a command id.</summary>
        /// <param name="commandId">The command id to check.</param>
        /// <returns>True if allowlisted.</returns>
        public bool IsAllowlisted(string commandId) =>
            !string.IsNullOrEmpty(commandId) && _actionAllowlist.Contains(commandId);

        /// <inheritdoc/>
        public MolcaAuthorizationDecision Authorize(MolcaCommandDefinition command, MolcaCommandContext context)
        {
            if (command == null)
                return MolcaAuthorizationDecision.Refuse("policy.unknown_command", "No command supplied.");

            // Read-only commands are safe under every profile.
            if (command.Kind == MolcaCommandKind.ReadOnly)
                return MolcaAuthorizationDecision.Allow();

            switch (_profile)
            {
                case MolcaAutomationProfile.Observe:
                    return MolcaAuthorizationDecision.Refuse("policy.observe_readonly",
                        "The Observe profile permits read-only commands only.");

                case MolcaAutomationProfile.Develop:
                    if (!IsAllowlisted(command.Id))
                        return NotAllowlisted(command);
                    // Irreversible actions must be confirmed; undoable ones may run.
                    return command.RequiresConfirmation && !context.IsConfirmed
                        ? MolcaAuthorizationDecision.RequireConfirmation(
                            "This action is irreversible and requires confirmation under the Develop profile.")
                        : MolcaAuthorizationDecision.Allow();

                case MolcaAutomationProfile.Release:
                    if (!IsAllowlisted(command.Id))
                        return NotAllowlisted(command);
                    // Environment-constraint checks are a Phase 3 addition; confirmation still gates irreversibles.
                    return command.RequiresConfirmation && !context.IsConfirmed
                        ? MolcaAuthorizationDecision.RequireConfirmation(
                            "This action is irreversible and requires confirmation under the Release profile.")
                        : MolcaAuthorizationDecision.Allow();

                case MolcaAutomationProfile.UnattendedCi:
                    // Exact allowlist, no interactive confirmation — an allowlisted action runs directly.
                    return IsAllowlisted(command.Id)
                        ? MolcaAuthorizationDecision.Allow()
                        : NotAllowlisted(command);

                default:
                    return MolcaAuthorizationDecision.Refuse("policy.unknown_profile",
                        $"Unknown policy profile '{_profile}'.");
            }
        }

        private static MolcaAuthorizationDecision NotAllowlisted(MolcaCommandDefinition command) =>
            MolcaAuthorizationDecision.Refuse("policy.not_allowlisted",
                $"Action '{command.Id}' is not in the automation action allowlist.");
    }
}
