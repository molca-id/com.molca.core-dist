using System;
using System.Collections.Generic;
using System.Linq;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>Whether a scene reference can be satisfied under a declared load set.</summary>
    public enum ReferenceSceneAvailability
    {
        /// <summary>The target scene is loaded whenever the owner is.</summary>
        Available = 0,

        /// <summary>The target scene may be loaded later, so the reference must tolerate a wait.</summary>
        Deferred = 1,

        /// <summary>The target scene is never loaded alongside the owner. The reference cannot resolve.</summary>
        Unavailable = 2,

        /// <summary>No load set covers the owner scene, so nothing can be concluded.</summary>
        Unknown = 3,
    }

    /// <summary>
    /// A declared set of scenes that are loaded together, and the scenes that may join them later.
    /// </summary>
    /// <remarks>
    /// Cross-scene references can only be validated against a statement of what is loaded when.
    /// Without one, tooling has to assume either that every enabled scene is simultaneously
    /// available — which reports nothing and misses real breakage — or that only the owner's own
    /// scene is — which floods an additively-loaded project with false errors. Both were wrong in
    /// opposite directions, which is why cross-scene wiring went unchecked.
    /// </remarks>
    public sealed class ReferenceLoadSet
    {
        /// <summary>Stable identifier for this set.</summary>
        public string Id { get; }

        /// <summary>The scene loaded first, which the others join.</summary>
        public string EntryScene { get; }

        /// <summary>Scenes guaranteed loaded alongside <see cref="EntryScene"/>.</summary>
        public IReadOnlyList<string> ConcurrentScenes { get; }

        /// <summary>Scenes that may be loaded later, during play.</summary>
        public IReadOnlyList<string> DeferredScenes { get; }

        /// <summary>
        /// True when nobody authored this set and it was guessed from the build settings.
        /// </summary>
        /// <remarks>
        /// Surfaced everywhere it is used. An inferred set is a starting point, not a statement of
        /// intent, and reporting a finding derived from a guess as though it were authored fact is
        /// how validation loses the reader's trust.
        /// </remarks>
        public bool IsInferred { get; }

        /// <summary>Every scene this set mentions, entry and concurrent and deferred.</summary>
        public IReadOnlyList<string> AllScenes { get; }

        /// <summary>Creates a load set.</summary>
        /// <param name="id">Stable identifier.</param>
        /// <param name="entryScene">The scene loaded first.</param>
        /// <param name="concurrentScenes">Scenes guaranteed loaded alongside the entry scene.</param>
        /// <param name="deferredScenes">Scenes that may be loaded later.</param>
        /// <param name="isInferred">True when this set was guessed rather than authored.</param>
        public ReferenceLoadSet(
            string id,
            string entryScene,
            IEnumerable<string> concurrentScenes = null,
            IEnumerable<string> deferredScenes = null,
            bool isInferred = false)
        {
            Id = string.IsNullOrEmpty(id) ? "default" : id;
            EntryScene = entryScene ?? string.Empty;
            IsInferred = isInferred;

            ConcurrentScenes = Normalise(concurrentScenes, EntryScene);
            DeferredScenes = Normalise(deferredScenes, EntryScene)
                .Where(s => !ConcurrentScenes.Contains(s, StringComparer.Ordinal))
                .ToList();

            var all = new List<string>();
            if (!string.IsNullOrEmpty(EntryScene))
                all.Add(EntryScene);
            all.AddRange(ConcurrentScenes);
            all.AddRange(DeferredScenes);
            AllScenes = all;
        }

        /// <summary>Trims, drops empties and duplicates, and excludes the entry scene.</summary>
        private static IReadOnlyList<string> Normalise(IEnumerable<string> scenes, string entryScene) =>
            (scenes ?? Array.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Where(s => !string.Equals(s, entryScene, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();

        /// <summary>True when this set says anything about <paramref name="scenePath"/>.</summary>
        /// <param name="scenePath">Project-relative scene path.</param>
        public bool Mentions(string scenePath) =>
            !string.IsNullOrEmpty(scenePath) && AllScenes.Contains(scenePath, StringComparer.Ordinal);

        /// <summary>
        /// Whether a reference from <paramref name="ownerScene"/> to <paramref name="targetScene"/> can
        /// resolve under this set.
        /// </summary>
        /// <param name="ownerScene">The scene holding the reference.</param>
        /// <param name="targetScene">The scene holding the provider.</param>
        public ReferenceSceneAvailability Evaluate(string ownerScene, string targetScene)
        {
            if (string.IsNullOrEmpty(ownerScene) || string.IsNullOrEmpty(targetScene))
                return ReferenceSceneAvailability.Unknown;

            if (!Mentions(ownerScene))
                return ReferenceSceneAvailability.Unknown;

            // Same scene is always available; no load set can make an object unreachable from itself.
            if (string.Equals(ownerScene, targetScene, StringComparison.Ordinal))
                return ReferenceSceneAvailability.Available;

            bool ownerIsResident = IsResident(ownerScene);
            bool targetIsResident = IsResident(targetScene);

            if (targetIsResident && ownerIsResident)
                return ReferenceSceneAvailability.Available;

            if (DeferredScenes.Contains(targetScene, StringComparer.Ordinal))
                return ReferenceSceneAvailability.Deferred;

            if (targetIsResident)
            {
                // The owner is deferred and the target is resident: by the time the owner exists, the
                // target already does.
                return ReferenceSceneAvailability.Available;
            }

            return ReferenceSceneAvailability.Unavailable;
        }

        /// <summary>True when a scene is part of the always-loaded core of this set.</summary>
        private bool IsResident(string scenePath) =>
            string.Equals(scenePath, EntryScene, StringComparison.Ordinal) ||
            ConcurrentScenes.Contains(scenePath, StringComparer.Ordinal);

        /// <summary>One-line description for the Hub and for finding messages.</summary>
        public string Describe()
        {
            string origin = IsInferred ? " (inferred)" : string.Empty;
            return $"{Id}{origin}: entry '{ShortName(EntryScene)}', "
                + $"{ConcurrentScenes.Count} concurrent, {DeferredScenes.Count} deferred";
        }

        private static string ShortName(string scenePath)
        {
            if (string.IsNullOrEmpty(scenePath))
                return "<none>";

            int slash = scenePath.LastIndexOf('/');
            string name = slash >= 0 ? scenePath.Substring(slash + 1) : scenePath;
            return name.EndsWith(".unity", StringComparison.Ordinal)
                ? name.Substring(0, name.Length - ".unity".Length)
                : name;
        }

        /// <inheritdoc/>
        public override string ToString() => Describe();
    }
}
