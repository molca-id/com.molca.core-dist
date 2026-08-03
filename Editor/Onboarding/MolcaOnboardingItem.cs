using System;
using System.Collections.Generic;

namespace Molca.Editor.Onboarding
{
    /// <summary>
    /// Whether an onboarding item states a fact about the project or an opinion about it.
    /// </summary>
    /// <remarks>
    /// <para>The severity line is the one thing this surface may not blur, and it is drawn in exactly one
    /// place: <b>an audit finding is <see cref="Required"/>; an opinion is <see cref="Recommended"/>.</b>
    /// An audit engine only emits a finding for something it asserts is wrong, so a domain with findings is
    /// misconfigured by definition. The project starter, by contrast, describes what a fully-featured Molca
    /// project looks like — a project that declines telemetry is not broken.</para>
    /// <para>This is the same boundary <see cref="Starter.IMolcaStarterStep"/> is built on: modelling the
    /// starter's opinions as findings would make the audit report "you could enable more things", which
    /// manufactures findings and teaches people to stop reading them. Rendering both in one checklist is
    /// only honest while the two stay separately labelled.</para>
    /// </remarks>
    public enum MolcaOnboardingSeverity
    {
        /// <summary>Something the project is asserted to have got wrong. Sourced from an audit finding.</summary>
        Required,

        /// <summary>Something the framework suggests. Never sourced from a finding; never implied by one.</summary>
        Recommended,
    }

    /// <summary>Where one onboarding item stands right now.</summary>
    public enum MolcaOnboardingStatus
    {
        /// <summary>Nothing left to do.</summary>
        Done,

        /// <summary>Actionable: running the item's action would change something.</summary>
        Todo,

        /// <summary>Actionable in principle, but something else has to happen first.</summary>
        Blocked,

        /// <summary>Does not apply to this project, so it counts against nothing.</summary>
        NotApplicable,
    }

    /// <summary>The evaluated state of one onboarding item, with the detail line shown beneath it.</summary>
    public readonly struct MolcaOnboardingCheck
    {
        /// <summary>Creates a check result.</summary>
        /// <param name="status">Where the item stands.</param>
        /// <param name="detail">One line of specifics, shown verbatim. Never <c>null</c>.</param>
        public MolcaOnboardingCheck(MolcaOnboardingStatus status, string detail)
        {
            Status = status;
            Detail = detail ?? string.Empty;
        }

        /// <summary>Where the item stands.</summary>
        public MolcaOnboardingStatus Status { get; }

        /// <summary>Specifics for this evaluation, e.g. a path, a count, or why it is blocked.</summary>
        public string Detail { get; }

        /// <summary>Whether this state should be counted as outstanding work.</summary>
        public bool IsOutstanding =>
            Status == MolcaOnboardingStatus.Todo || Status == MolcaOnboardingStatus.Blocked;

        /// <summary>Nothing left to do.</summary>
        /// <param name="detail">What is already in place.</param>
        /// <returns>A done result.</returns>
        public static MolcaOnboardingCheck Done(string detail) =>
            new MolcaOnboardingCheck(MolcaOnboardingStatus.Done, detail);

        /// <summary>Actionable now.</summary>
        /// <param name="detail">What is missing.</param>
        /// <returns>A todo result.</returns>
        public static MolcaOnboardingCheck Todo(string detail) =>
            new MolcaOnboardingCheck(MolcaOnboardingStatus.Todo, detail);

        /// <summary>Waiting on something else.</summary>
        /// <param name="reason">What has to happen first.</param>
        /// <returns>A blocked result.</returns>
        public static MolcaOnboardingCheck Blocked(string reason) =>
            new MolcaOnboardingCheck(MolcaOnboardingStatus.Blocked, reason);

        /// <summary>Not relevant to this project.</summary>
        /// <param name="reason">Why it does not apply.</param>
        /// <returns>A not-applicable result.</returns>
        public static MolcaOnboardingCheck NotApplicable(string reason) =>
            new MolcaOnboardingCheck(MolcaOnboardingStatus.NotApplicable, reason);
    }

    /// <summary>
    /// One row of the onboarding checklist: a check that can report where the project stands, and — when
    /// there is a single correct next move — an action that advances it.
    /// </summary>
    /// <remarks>
    /// <para><b>An item owns no state.</b> Its status is derived by re-running <see cref="Check"/>, never
    /// recorded. That is the whole reason this is a validation checklist rather than a wizard: there is no
    /// "step 3 complete" flag to go stale when a teammate deletes the asset that satisfied it.</para>
    /// <para><b>Evaluation never mutates.</b> <see cref="Check"/> runs whenever the surface refreshes, so it
    /// must be read-only and cheap — the same rule <see cref="Remediation.IMolcaFix"/> states as "never
    /// mutate on a scan". An item whose real check is an expensive project-wide sweep must report what is
    /// already known and let <see cref="Act"/> navigate to the surface that runs the sweep, rather than run
    /// it here.</para>
    /// <para><b>An item may navigate instead of acting.</b> Where the decision belongs to the author,
    /// <see cref="Act"/> should open the surface that owns it rather than guess — this is how the checklist
    /// teaches the editor without masking or spotlighting any of it.</para>
    /// <para>Editor-only; main thread.</para>
    /// </remarks>
    public sealed class MolcaOnboardingItem
    {
        /// <summary>Creates an onboarding item.</summary>
        /// <param name="id">Stable, globally-unique id (e.g. <c>onboarding.mcp-proxy</c>).</param>
        /// <param name="title">Short row title.</param>
        /// <param name="summary">What this is, in the user's terms.</param>
        /// <param name="check">Read-only evaluation of where the project stands. Called on every refresh.</param>
        /// <param name="severity">Finding-backed (<see cref="MolcaOnboardingSeverity.Required"/>) or opinion.</param>
        /// <param name="order">Sort order; items others depend on come first.</param>
        /// <param name="actionLabel">Label for the action button; <c>null</c> renders no button.</param>
        /// <param name="act">The action. <c>null</c> makes the item informational.</param>
        /// <param name="why">Optional one line on why it matters — the teaching half of the row.</param>
        /// <param name="docId">Optional reference-doc id for a "Learn more" link (see <c>MolcaDocEntry.Id</c>).</param>
        public MolcaOnboardingItem(
            string id,
            string title,
            string summary,
            Func<MolcaOnboardingCheck> check,
            MolcaOnboardingSeverity severity = MolcaOnboardingSeverity.Recommended,
            int order = 100,
            string actionLabel = null,
            Action act = null,
            string why = null,
            string docId = null)
        {
            Id = id;
            Title = title;
            Summary = summary;
            Check = check;
            Severity = severity;
            Order = order;
            ActionLabel = actionLabel;
            Act = act;
            Why = why;
            DocId = docId;
        }

        /// <summary>Stable, globally-unique id; the checklist rejects duplicates.</summary>
        public string Id { get; }

        /// <summary>Short row title.</summary>
        public string Title { get; }

        /// <summary>What this is, in the user's terms.</summary>
        public string Summary { get; }

        /// <summary>Why it matters; may be <c>null</c>.</summary>
        public string Why { get; }

        /// <summary>Read-only evaluation of where the project stands.</summary>
        public Func<MolcaOnboardingCheck> Check { get; }

        /// <summary>Whether this row is finding-backed or an opinion.</summary>
        public MolcaOnboardingSeverity Severity { get; }

        /// <summary>Sort order among items.</summary>
        public int Order { get; }

        /// <summary>Action button label, or <c>null</c> for an informational row.</summary>
        public string ActionLabel { get; }

        /// <summary>Runs the action, or navigates to the surface that owns the decision.</summary>
        public Action Act { get; }

        /// <summary>Reference-doc id for the "Learn more" link, or <c>null</c>.</summary>
        public string DocId { get; }
    }

    /// <summary>
    /// Contributes rows to the onboarding checklist. Discovered by <c>TypeCache</c>; needs a public
    /// parameterless constructor.
    /// </summary>
    /// <remarks>
    /// The seam an SDK, fork, or add-on uses to put its own setup in front of a new author without Core
    /// knowing it exists — the same shape as <see cref="Remediation.IMolcaRemediationDomainProvider"/> and
    /// <see cref="Starter.IMolcaStarterStep"/>. <see cref="GetItems"/> runs while the view builds, so it must
    /// be cheap and side-effect free: the work belongs in each item's <see cref="MolcaOnboardingItem.Check"/>
    /// and <see cref="MolcaOnboardingItem.Act"/>.
    /// </remarks>
    public interface IMolcaOnboardingItemProvider
    {
        /// <summary>Returns the items this provider contributes; never <c>null</c>.</summary>
        /// <returns>Checklist rows.</returns>
        IEnumerable<MolcaOnboardingItem> GetItems();
    }
}
