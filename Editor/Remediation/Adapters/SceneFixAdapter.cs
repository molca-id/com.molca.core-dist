using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Molca.Editor.Doctor;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Remediation.Adapters
{
    /// <summary>
    /// Projects one scene-audit <see cref="ISceneFix"/> onto <see cref="IMolcaFix"/> so scene fixes take part
    /// in the shared remediation pass without changing their public contract.
    /// </summary>
    /// <remarks>
    /// <see cref="ISceneFix"/> predates the unified contract and stays the authoring surface for scene fixes
    /// (Sprint 55 shipped four of them, and forks may ship more). It has no destructive/deterministic facets
    /// because every scene fix is single-answer and additive by construction — the adapter supplies those
    /// facets and derives the finding code as <c>scene.{HandledCheckId}</c>.
    /// </remarks>
    [MolcaFixSuppliedByContributor]
    internal sealed class SceneFixAdapter : IMolcaFix
    {
        /// <summary>The finding-code prefix scene-audit check ids are namespaced under.</summary>
        internal const string CodePrefix = "scene.";

        private readonly ISceneFix _inner;

        /// <summary>Wraps a scene fix.</summary>
        /// <param name="inner">The scene fix to project.</param>
        internal SceneFixAdapter(ISceneFix inner) => _inner = inner;

        /// <summary>Builds the unified finding code for a scene-audit check id.</summary>
        /// <param name="checkId">A scene-audit check id, e.g. <c>scene-texture-budget</c>.</param>
        /// <returns>The namespaced finding code, e.g. <c>scene.scene-texture-budget</c>.</returns>
        internal static string CodeFor(string checkId) => CodePrefix + checkId;

        /// <inheritdoc/>
        public string Id => _inner.Id;

        /// <inheritdoc/>
        public string Description => _inner.Description;

        /// <inheritdoc/>
        public IReadOnlyCollection<string> HandledFindingCodes => new[] { CodeFor(_inner.HandledCheckId) };

        /// <inheritdoc/>
        /// <remarks>Scene fixes take no arguments, so every one of them is deterministic.</remarks>
        public bool IsDeterministic => true;

        /// <inheritdoc/>
        /// <remarks>
        /// No scene fix discards authored data — the importer-setting fix changes a build-time setting and
        /// reverts from its file snapshot, which the <see cref="FixReversibility"/> facet already expresses.
        /// </remarks>
        public bool IsDestructive => false;

        /// <inheritdoc/>
        public FixReversibility Reversibility => _inner.Reversibility;

        /// <inheritdoc/>
        public MolcaFixOutcome Apply(
            MolcaFixTarget target, bool dryRun, JObject args, CancellationToken cancellationToken)
        {
            var outcome = _inner.Apply(target.Path, dryRun, cancellationToken);
            return new MolcaFixOutcome(
                outcome.Applied, outcome.Message, outcome.Before, outcome.After, outcome.UndoEntryId);
        }
    }

    /// <summary>
    /// Contributes every registered <see cref="ISceneFix"/> to <see cref="MolcaFixRegistry"/> as an adapted
    /// <see cref="IMolcaFix"/>.
    /// </summary>
    /// <remarks>
    /// Scene fixes cannot be discovered directly by the unified registry — they implement a different
    /// interface — so this contributor is the bridge. Ids are unchanged, so a scene fix keeps its identity in
    /// both registries.
    /// </remarks>
    public sealed class SceneFixContributor : IMolcaFixContributor
    {
        /// <inheritdoc/>
        public IEnumerable<IMolcaFix> Contribute() =>
            SceneFixRegistry.All.Select(fix => (IMolcaFix)new SceneFixAdapter(fix)).ToList();
    }
}
