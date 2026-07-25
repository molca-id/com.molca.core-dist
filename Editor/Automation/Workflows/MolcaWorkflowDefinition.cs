using System.Collections.Generic;
using System.Linq;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// A named, ordered composition of <see cref="MolcaWorkflowStep"/>s (§11) — e.g. Preflight, Runtime
    /// Smoke, Content Verify. Run by <see cref="MolcaWorkflowRunner"/> and exposed to every transport as a
    /// neutral command through <see cref="MolcaWorkflowCommandAdapter"/>, so a workflow shares the same
    /// policy, mode, resource, progress, and audit model as any other command.
    /// </summary>
    public sealed class MolcaWorkflowDefinition
    {
        /// <summary>Stable command id for this workflow (e.g. <c>molca.preflight</c>).</summary>
        public string Id { get; }

        /// <summary>Human-facing display name.</summary>
        public string DisplayName { get; }

        /// <summary>Description of the workflow's purpose.</summary>
        public string Description { get; }

        /// <summary>The ordered steps. Never null or empty.</summary>
        public IReadOnlyList<MolcaWorkflowStep> Steps { get; }

        /// <summary>When true, any warning diagnostic fails the workflow (§11.1 fail-on-warning).</summary>
        public bool FailOnWarning { get; }

        /// <summary>The editor mode the workflow requires.</summary>
        public MolcaCommandMode Mode { get; }

        /// <summary>Read-only vs. action classification, for policy (§9.3). Defaults to read-only.</summary>
        public MolcaCommandKind Kind { get; }

        /// <summary>The resources the workflow claims for coordination (§13).</summary>
        public IReadOnlyList<MolcaResourceClaim> ResourceClaims { get; }

        /// <summary>How an action workflow's effect can be reverted (§8). Meaningful only when <see cref="Kind"/> is Action.</summary>
        public MolcaCommandReversibility Reversibility { get; }

        /// <summary>Whether an irreversible action workflow requires interactive confirmation before running.</summary>
        public bool RequiresConfirmation { get; }

        /// <summary>Creates a workflow definition.</summary>
        /// <param name="id">Stable command id.</param>
        /// <param name="displayName">Display name.</param>
        /// <param name="description">Description.</param>
        /// <param name="steps">Ordered steps (at least one).</param>
        /// <param name="failOnWarning">Whether a warning fails the workflow. Defaults to false.</param>
        /// <param name="mode">Required editor mode. Defaults to <see cref="MolcaCommandMode.Edit"/>.</param>
        /// <param name="kind">Read-only vs. action. Defaults to <see cref="MolcaCommandKind.ReadOnly"/>.</param>
        /// <param name="resourceClaims">Resource claims. Defaults to <see cref="MolcaResourceClaim.ProjectRead"/>.</param>
        /// <param name="reversibility">Revert path for an action workflow. Defaults to <see cref="MolcaCommandReversibility.None"/>.</param>
        /// <param name="requiresConfirmation">Whether the workflow requires confirmation. Defaults to false.</param>
        /// <exception cref="System.ArgumentException">If id is empty or no steps are supplied.</exception>
        public MolcaWorkflowDefinition(
            string id, string displayName, string description,
            IReadOnlyList<MolcaWorkflowStep> steps,
            bool failOnWarning = false,
            MolcaCommandMode mode = MolcaCommandMode.Edit,
            MolcaCommandKind kind = MolcaCommandKind.ReadOnly,
            IReadOnlyList<MolcaResourceClaim> resourceClaims = null,
            MolcaCommandReversibility reversibility = MolcaCommandReversibility.None,
            bool requiresConfirmation = false)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new System.ArgumentException("Workflow id must be non-empty.", nameof(id));
            if (steps == null || steps.Count == 0)
                throw new System.ArgumentException($"Workflow '{id}' must declare at least one step.", nameof(steps));

            Id = id;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? id : displayName;
            Description = description ?? string.Empty;
            Steps = steps.ToArray();
            FailOnWarning = failOnWarning;
            Mode = mode;
            Kind = kind;
            ResourceClaims = resourceClaims != null && resourceClaims.Count > 0
                ? resourceClaims.ToArray()
                : new[] { MolcaResourceClaim.ProjectRead };
            Reversibility = reversibility;
            RequiresConfirmation = requiresConfirmation;
        }
    }
}
