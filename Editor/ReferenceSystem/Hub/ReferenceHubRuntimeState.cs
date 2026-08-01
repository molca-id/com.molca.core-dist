using System;
using System.Collections.Generic;
using System.Linq;
using Molca.ReferenceSystem;

namespace Molca.Editor.ReferenceSystem.Hub
{
    /// <summary>One live runtime registration, as the Runtime view shows it.</summary>
    internal sealed class ReferenceHubRuntimeEntry
    {
        /// <summary>The full scoped key, in <see cref="ReferenceRuntimeKey.ToString"/> form.</summary>
        internal string Key { get; }

        /// <summary>The registered <c>(RefType, RefId)</c> pair, without the scope.</summary>
        internal string ShortKey { get; }

        /// <summary>The space this registration's id is unique in.</summary>
        internal ReferenceScopeKind ScopeKind { get; }

        /// <summary>Which instance of <see cref="ScopeKind"/> this registration belongs to.</summary>
        internal string ScopeId { get; }

        /// <summary>The registered object's display name, or a note when it has been destroyed.</summary>
        internal string DisplayName { get; }

        /// <summary>The concrete registered type.</summary>
        internal string TypeName { get; }

        /// <summary>
        /// True when the editor audit also found a provider with this exact key — i.e. the thing the editor
        /// believes should be registered actually is.
        /// </summary>
        internal bool MatchesAudit { get; }

        /// <summary>
        /// True when this registration is scoped, so the audit's project-wide key list cannot speak
        /// to it either way.
        /// </summary>
        internal bool IsScoped =>
            ScopeKind == ReferenceScopeKind.Scene || ScopeKind == ReferenceScopeKind.PrefabLocal;

        internal ReferenceHubRuntimeEntry(
            ReferenceRuntimeKey key, string displayName, string typeName, bool matchesAudit)
        {
            Key = key.ToString();
            ShortKey = $"{key.RefType}:{key.RefId}";
            ScopeKind = key.ScopeKind;
            ScopeId = key.ScopeId;
            DisplayName = displayName ?? string.Empty;
            TypeName = typeName ?? string.Empty;
            MatchesAudit = matchesAudit;
        }
    }

    /// <summary>
    /// A read-only snapshot of the live <see cref="ReferenceManager"/> registry, compared against the editor
    /// audit's expectations.
    /// </summary>
    /// <remarks>
    /// <para>The comparison is the point of the view. The editor audit tells you what <i>should</i> be
    /// registered from serialized data; the registry tells you what <i>is</i>. A key the audit found but the
    /// runtime never registered means a provider on a disabled object, an unloaded scene, or a lifecycle
    /// mistake — and until now that class of bug was only visible as a reference that failed to resolve, with
    /// no way to see which side was wrong.</para>
    ///
    /// <para>Scoped registrations are held apart from that comparison rather than folded into it. A
    /// prefab-local id is not project-unique, so the audit's project-wide key list can neither confirm
    /// nor contradict it; counting one as "registered but unknown" would report every scoped prefab
    /// as a discrepancy.</para>
    ///
    /// <para>Reading only: this never registers, unregisters, or resolves anything.</para>
    /// </remarks>
    internal sealed class ReferenceHubRuntimeState
    {
        /// <summary>Whether a live registry could be reached at all.</summary>
        internal bool IsAvailable { get; }

        /// <summary>Why the registry is unavailable, when it is.</summary>
        internal string UnavailableReason { get; }

        /// <summary>Live registrations, ordered by key.</summary>
        internal IReadOnlyList<ReferenceHubRuntimeEntry> Entries { get; }

        /// <summary>Registrations per reference type.</summary>
        internal IReadOnlyList<KeyValuePair<string, int>> PerType { get; }

        /// <summary>
        /// Keys the editor audit expected to be registered but which the runtime registry does not hold.
        /// </summary>
        internal IReadOnlyList<string> ExpectedButMissing { get; }

        /// <summary>
        /// Global-scope keys the runtime holds that the editor audit did not find — a runtime-created
        /// provider, or a scene the audit's scope did not cover.
        /// </summary>
        internal IReadOnlyList<string> RegisteredButUnknown { get; }

        /// <summary>How many prefab scope instances are currently open.</summary>
        internal int OpenScopeCount { get; }

        /// <summary>The most recent registry events, oldest first.</summary>
        internal IReadOnlyList<ReferenceDiagnostic> Diagnostics { get; }

        private ReferenceHubRuntimeState(
            bool isAvailable, string unavailableReason,
            IReadOnlyList<ReferenceHubRuntimeEntry> entries,
            IReadOnlyList<KeyValuePair<string, int>> perType,
            IReadOnlyList<string> expectedButMissing,
            IReadOnlyList<string> registeredButUnknown,
            int openScopeCount = 0,
            IReadOnlyList<ReferenceDiagnostic> diagnostics = null)
        {
            IsAvailable = isAvailable;
            UnavailableReason = unavailableReason ?? string.Empty;
            Entries = entries ?? Array.Empty<ReferenceHubRuntimeEntry>();
            PerType = perType ?? Array.Empty<KeyValuePair<string, int>>();
            ExpectedButMissing = expectedButMissing ?? Array.Empty<string>();
            RegisteredButUnknown = registeredButUnknown ?? Array.Empty<string>();
            OpenScopeCount = openScopeCount;
            Diagnostics = diagnostics ?? Array.Empty<ReferenceDiagnostic>();
        }

        /// <summary>
        /// Reads the live registry and compares it against <paramref name="snapshot"/>.
        /// </summary>
        /// <param name="snapshot">The editor audit to compare against. Null skips the comparison.</param>
        /// <param name="isPlaying">Whether the editor is in Play Mode.</param>
        /// <returns>The runtime state; never null.</returns>
        internal static ReferenceHubRuntimeState Read(ReferenceAuditSnapshot snapshot, bool isPlaying)
        {
            if (!isPlaying)
            {
                return Unavailable(
                    "The reference registry only exists while the game is running. Enter Play Mode to see "
                    + "live registrations.");
            }

            ReferenceManager manager;
            try
            {
                manager = RuntimeManager.GetSubsystem<ReferenceManager>();
            }
            catch (Exception e)
            {
                return Unavailable($"The reference registry could not be read: {e.Message}");
            }

            if (manager == null)
            {
                return Unavailable(
                    "No ReferenceManager subsystem is registered with the RuntimeManager in this scene, so "
                    + "no reference can resolve at runtime.");
            }

            var entries = new List<ReferenceHubRuntimeEntry>();
            var liveGlobalKeys = new HashSet<string>(StringComparer.Ordinal);
            var expected = ExpectedKeys(snapshot);

            foreach (var key in manager.GetAllKeys())
            {
                bool isGlobal = key.TryToLegacyId(out var legacyId);
                string comparable = isGlobal ? legacyId.ToString() : null;

                if (isGlobal)
                    liveGlobalKeys.Add(comparable);

                var referenceable = manager.Get(key);
                entries.Add(new ReferenceHubRuntimeEntry(
                    key,
                    // A registration whose object has been destroyed is exactly the state the runtime lookups
                    // now purge, so seeing one here is worth reporting rather than rendering as blank.
                    referenceable?.DisplayName ?? "<destroyed or purged>",
                    referenceable?.GetType().Name ?? string.Empty,
                    isGlobal && expected.Contains(comparable)));
            }

            entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            return new ReferenceHubRuntimeState(
                isAvailable: true,
                unavailableReason: null,
                entries: entries,
                perType: manager.GetRegistrationStats()
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .ToList(),
                expectedButMissing: expected.Where(k => !liveGlobalKeys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList(),
                registeredButUnknown: liveGlobalKeys.Where(k => !expected.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList(),
                openScopeCount: manager.OpenScopeCount,
                diagnostics: manager.Diagnostics.Snapshot());
        }

        /// <summary>
        /// The <c>(RefType, RefId)</c> keys the audit expects the runtime to hold: runtime-resolvable
        /// providers with an id. A prefab-asset or ScriptableObject provider is deliberately excluded — it is
        /// never registered, so counting it as missing would report every project as broken.
        /// </summary>
        private static HashSet<string> ExpectedKeys(ReferenceAuditSnapshot snapshot) =>
            snapshot == null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(
                    snapshot.Providers
                        .Where(p => p.IsRuntimeResolvable && !string.IsNullOrEmpty(p.RefId))
                        .Select(p => $"{p.RefType}:{p.RefId}"),
                    StringComparer.Ordinal);

        private static ReferenceHubRuntimeState Unavailable(string reason) =>
            new ReferenceHubRuntimeState(false, reason, null, null, null, null);

        /// <summary>One-line summary for the Runtime view header.</summary>
        internal string Describe()
        {
            if (!IsAvailable)
                return UnavailableReason;

            var summary = $"{Entries.Count} live registration{(Entries.Count == 1 ? "" : "s")}";

            int scoped = Entries.Count(e => e.IsScoped);
            if (scoped > 0)
                summary += $" · {scoped} scoped across {OpenScopeCount} open scope{(OpenScopeCount == 1 ? "" : "s")}";
            if (ExpectedButMissing.Count > 0)
                summary += $" · {ExpectedButMissing.Count} expected but not registered";
            if (RegisteredButUnknown.Count > 0)
                summary += $" · {RegisteredButUnknown.Count} registered but not in the audit scope";

            int problems = Diagnostics.Count(d => d.IsProblem);
            if (problems > 0)
                summary += $" · {problems} problem event{(problems == 1 ? "" : "s")}";

            return summary;
        }
    }
}
