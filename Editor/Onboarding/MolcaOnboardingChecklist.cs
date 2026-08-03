using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Onboarding
{
    /// <summary>One item paired with the result of evaluating it.</summary>
    public sealed class MolcaOnboardingEntry
    {
        /// <summary>Creates an evaluated entry.</summary>
        /// <param name="item">The item.</param>
        /// <param name="check">Its evaluation.</param>
        public MolcaOnboardingEntry(MolcaOnboardingItem item, MolcaOnboardingCheck check)
        {
            Item = item;
            Check = check;
        }

        /// <summary>The item.</summary>
        public MolcaOnboardingItem Item { get; }

        /// <summary>Where it stands as of this snapshot.</summary>
        public MolcaOnboardingCheck Check { get; }
    }

    /// <summary>
    /// The evaluated checklist at one moment. Explicitly a snapshot, not a live view: it is produced by an
    /// evaluation and never updates itself.
    /// </summary>
    public sealed class MolcaOnboardingSnapshot
    {
        /// <summary>Creates a snapshot.</summary>
        /// <param name="entries">Evaluated entries, in render order.</param>
        public MolcaOnboardingSnapshot(IReadOnlyList<MolcaOnboardingEntry> entries)
            => Entries = entries ?? Array.Empty<MolcaOnboardingEntry>();

        /// <summary>Every evaluated entry, in render order.</summary>
        public IReadOnlyList<MolcaOnboardingEntry> Entries { get; }

        /// <summary>Outstanding entries at <see cref="MolcaOnboardingSeverity.Required"/>, of either kind.</summary>
        public int RequiredOutstanding => Entries.Count(
            e => e.Item.Severity == MolcaOnboardingSeverity.Required && e.Check.IsOutstanding);

        /// <summary>
        /// Required entries a Molca audit actually asserts are wrong, as distinct from ones nobody has
        /// checked yet.
        /// </summary>
        /// <remarks>
        /// The two must be counted apart wherever severity is rendered. A domain that has never been audited
        /// is outstanding work, but the project has not been accused of anything — showing it in the same red
        /// as a confirmed finding makes a healthy new project look broken, and a surface that cries wolf on
        /// day one is not read on day ten.
        /// </remarks>
        public int RequiredFindings => Entries.Count(
            e => e.Item.Severity == MolcaOnboardingSeverity.Required
                 && e.Check.Status == MolcaOnboardingStatus.Todo);

        /// <summary>Required entries that are outstanding only because nothing has established their state.</summary>
        public int RequiredUnchecked => RequiredOutstanding - RequiredFindings;

        /// <summary>Outstanding entries the framework merely suggests.</summary>
        public int RecommendedOutstanding => Entries.Count(
            e => e.Item.Severity == MolcaOnboardingSeverity.Recommended && e.Check.IsOutstanding);

        /// <summary>Entries with nothing left to do.</summary>
        public int DoneCount => Entries.Count(e => e.Check.Status == MolcaOnboardingStatus.Done);

        /// <summary>Whether nothing is outstanding at either severity.</summary>
        public bool IsClear => RequiredOutstanding == 0 && RecommendedOutstanding == 0;

        /// <summary>A one-line summary for a header or a rail chip.</summary>
        /// <returns>Text such as <c>"2 required · 3 recommended"</c>.</returns>
        public string Summarize()
        {
            if (Entries.Count == 0) return "Nothing to check.";
            if (IsClear) return $"All clear · {DoneCount} checked";

            var parts = new List<string>(3);
            if (RequiredFindings > 0) parts.Add($"{RequiredFindings} required");
            if (RequiredUnchecked > 0) parts.Add($"{RequiredUnchecked} to check");
            if (RecommendedOutstanding > 0) parts.Add($"{RecommendedOutstanding} recommended");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// The project's onboarding checklist: every registered <see cref="MolcaOnboardingItem"/>, and the
    /// read-only evaluation of where the project stands against them.
    /// </summary>
    /// <remarks>
    /// <para><b>This owns no engine.</b> It is a projection over the surfaces that already exist —
    /// <see cref="Starter.MolcaStarter"/> for the opinionated setup and
    /// <see cref="Remediation.MolcaRemediationDomains"/> for the audits — plus whatever an SDK or fork
    /// contributes through <see cref="IMolcaOnboardingItemProvider"/>. It defines no findings, registers no
    /// fixes, and has no opinion of its own; adding one here would be a second source of truth for what a
    /// configured project looks like.</para>
    /// <para><b>Evaluation is explicit.</b> <see cref="Evaluate"/> runs when a surface asks it to — on open
    /// and on Refresh — never on a timer. Item checks are read-only by contract, but they are still work,
    /// and a checklist that silently re-scanned the project every repaint would be the most expensive
    /// window in the editor.</para>
    /// <para>Editor-only; main thread. Discovery is cached until <see cref="Reset"/>.</para>
    /// </remarks>
    public static class MolcaOnboardingChecklist
    {
        private static List<MolcaOnboardingItem> _items;

        /// <summary>Raised after every <see cref="Evaluate"/>, so a badge or an open view can repaint.</summary>
        public static event Action Changed;

        /// <summary>
        /// The most recent snapshot, or <c>null</c> when nothing has evaluated yet.
        /// </summary>
        /// <remarks>
        /// Deliberately a cache of the last <em>evaluation</em>, not of the project's state: a surface that
        /// wants a current answer calls <see cref="Evaluate"/>. This exists so a secondary surface — the Hub's
        /// activity chip — can show what is already known without triggering an evaluation of its own on
        /// every repaint.
        /// </remarks>
        public static MolcaOnboardingSnapshot LastSnapshot { get; private set; }

        /// <summary>Every registered item, in render order.</summary>
        public static IReadOnlyList<MolcaOnboardingItem> Items
        {
            get { EnsureDiscovered(); return _items; }
        }

        /// <summary>Clears the discovery cache and the last snapshot. Intended for tests.</summary>
        public static void Reset()
        {
            _items = null;
            LastSnapshot = null;
        }

        /// <summary>Returns <see cref="LastSnapshot"/>, evaluating once if nothing has yet.</summary>
        /// <returns>A snapshot; never <c>null</c>.</returns>
        public static MolcaOnboardingSnapshot EvaluateIfNeeded() => LastSnapshot ?? Evaluate();

        /// <summary>
        /// Evaluates every item and returns the snapshot.
        /// </summary>
        /// <returns>The snapshot; never <c>null</c>.</returns>
        /// <remarks>
        /// An item whose check throws is reported as <see cref="MolcaOnboardingStatus.Blocked"/> with the
        /// exception message rather than dropped. A row that vanishes when its own check fails is how a
        /// checklist quietly starts lying: the user reads "all clear" from a list that stopped looking.
        /// </remarks>
        public static MolcaOnboardingSnapshot Evaluate()
        {
            var entries = new List<MolcaOnboardingEntry>();

            foreach (var item in Items)
                entries.Add(new MolcaOnboardingEntry(item, EvaluateItem(item)));

            LastSnapshot = new MolcaOnboardingSnapshot(entries);
            Changed?.Invoke();
            return LastSnapshot;
        }

        /// <summary>Evaluates one item, converting a thrown check into a reported <c>Blocked</c>.</summary>
        /// <param name="item">The item to evaluate.</param>
        /// <returns>Its check result; never throws.</returns>
        internal static MolcaOnboardingCheck EvaluateItem(MolcaOnboardingItem item)
        {
            if (item?.Check == null)
                return MolcaOnboardingCheck.NotApplicable("This item reports no state.");

            try
            {
                return item.Check();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MolcaOnboarding] '{item.Id}' could not report its state: {ex.Message}");
                return MolcaOnboardingCheck.Blocked($"Could not be checked: {ex.Message}");
            }
        }

        private static void EnsureDiscovered()
        {
            if (_items != null) return;

            var found = new List<MolcaOnboardingItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var type in TypeCache.GetTypesDerivedFrom<IMolcaOnboardingItemProvider>())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null) continue;

                try
                {
                    var provider = (IMolcaOnboardingItemProvider)Activator.CreateInstance(type);
                    foreach (var item in provider.GetItems() ?? Enumerable.Empty<MolcaOnboardingItem>())
                    {
                        if (item == null || string.IsNullOrWhiteSpace(item.Id)) continue;
                        if (!seen.Add(item.Id))
                        {
                            Debug.LogWarning(
                                $"[MolcaOnboarding] Duplicate item id '{item.Id}' from '{type.FullName}'; skipped.");
                            continue;
                        }
                        found.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MolcaOnboarding] Provider '{type.FullName}' failed: {ex.Message}");
                }
            }

            _items = found
                .OrderBy(i => i.Severity == MolcaOnboardingSeverity.Required ? 0 : 1)
                .ThenBy(i => i.Order)
                .ThenBy(i => i.Id, StringComparer.Ordinal)
                .ToList();
        }
    }
}
