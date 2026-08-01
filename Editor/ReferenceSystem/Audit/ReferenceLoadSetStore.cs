using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>
    /// The project's declared scene load sets, read from <see cref="FilePath"/>, with an inferred
    /// fallback when none have been authored.
    /// </summary>
    /// <remarks>
    /// <para>Stored under <c>ProjectSettings/</c> and committed, unlike the severity overrides in
    /// <see cref="Hub.ReferenceHubPolicyStore"/>. The difference is deliberate: load sets describe
    /// how the game actually loads, so they must decide validation identically for every developer
    /// and for CI. A per-user setting that changed whether a build failed would be worse than having
    /// no setting at all.</para>
    ///
    /// <para>When nothing is authored, one set is inferred from the enabled build scenes and marked
    /// <see cref="ReferenceLoadSet.IsInferred"/>. Inference is a starting point, and every surface
    /// that uses it says so.</para>
    /// </remarks>
    public static class ReferenceLoadSetStore
    {
        /// <summary>Where the authored sets live, relative to the project root.</summary>
        public const string FilePath = "ProjectSettings/MolcaReferenceLoadSets.json";

        /// <summary>Schema version of the stored file.</summary>
        public const int SchemaVersion = 1;

        private static List<ReferenceLoadSet> _cached;
        private static bool _loaded;

        /// <summary>Raised when the authored sets change.</summary>
        public static event Action Changed;

        /// <summary>
        /// The load sets in force: the authored ones, or a single inferred set when none exist.
        /// Never empty unless the project has no scenes at all.
        /// </summary>
        public static IReadOnlyList<ReferenceLoadSet> Sets
        {
            get
            {
                if (!_loaded)
                {
                    _cached = Load();
                    _loaded = true;
                }

                return _cached;
            }
        }

        /// <summary>True when no set has been authored and the current ones were guessed.</summary>
        public static bool IsInferred => Sets.Count > 0 && Sets.All(s => s.IsInferred);

        /// <summary>Forgets the cache so the next read re-reads the file.</summary>
        public static void Invalidate()
        {
            _loaded = false;
            _cached = null;
            Changed?.Invoke();
        }

        /// <summary>
        /// Evaluates a cross-scene reference against every set that mentions the owner scene.
        /// </summary>
        /// <param name="ownerScene">The scene holding the reference.</param>
        /// <param name="targetScene">The scene holding the provider.</param>
        /// <returns>
        /// The worst availability across the sets that mention the owner, or
        /// <see cref="ReferenceSceneAvailability.Unknown"/> when none do.
        /// </returns>
        /// <remarks>
        /// The worst, not the best. A reference that works in one load set and cannot resolve in
        /// another is broken in that second one, and reporting the optimistic answer would hide
        /// exactly the configuration-dependent breakage load sets exist to catch.
        /// </remarks>
        public static ReferenceSceneAvailability Evaluate(string ownerScene, string targetScene)
        {
            var worst = ReferenceSceneAvailability.Unknown;
            bool sawAny = false;

            foreach (var set in Sets)
            {
                var result = set.Evaluate(ownerScene, targetScene);
                if (result == ReferenceSceneAvailability.Unknown)
                    continue;

                sawAny = true;
                if (result > worst || worst == ReferenceSceneAvailability.Unknown)
                    worst = result;
            }

            return sawAny ? worst : ReferenceSceneAvailability.Unknown;
        }

        /// <summary>Writes the authored sets, replacing whatever was there.</summary>
        /// <param name="sets">The sets to store. Inferred sets are not written.</param>
        /// <returns>True when the file was written.</returns>
        public static bool Save(IEnumerable<ReferenceLoadSet> sets)
        {
            var authored = (sets ?? Array.Empty<ReferenceLoadSet>()).Where(s => s != null && !s.IsInferred).ToList();

            try
            {
                var record = new LoadSetFile
                {
                    schemaVersion = SchemaVersion,
                    sets = authored.Select(LoadSetRecord.From).ToArray(),
                };

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath)) ?? ".");
                File.WriteAllText(FilePath, JsonUtility.ToJson(record, prettyPrint: true));
                Invalidate();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[ReferenceLoadSets] Could not write {FilePath}: {e.Message}");
                return false;
            }
        }

        /// <summary>Reads the authored sets, falling back to inference.</summary>
        private static List<ReferenceLoadSet> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var record = JsonUtility.FromJson<LoadSetFile>(File.ReadAllText(FilePath));

                    // An unreadable or future-schema file falls back to inference rather than to
                    // nothing: reporting "no load sets" would silently disable cross-scene validation
                    // for a project that has clearly configured it.
                    if (record != null && record.schemaVersion == SchemaVersion && record.sets != null)
                    {
                        var authored = record.sets.Where(s => s != null).Select(s => s.ToLoadSet()).ToList();
                        if (authored.Count > 0)
                            return authored;
                    }
                    else
                    {
                        Debug.LogWarning(
                            $"[ReferenceLoadSets] {FilePath} is unreadable or has an unsupported schema; "
                            + "falling back to an inferred load set.");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ReferenceLoadSets] Could not read {FilePath}: {e.Message}");
            }

            return Infer();
        }

        /// <summary>
        /// Guesses one load set from the enabled build scenes: the first is the entry scene and the
        /// rest are treated as deferred.
        /// </summary>
        /// <remarks>
        /// Deferred rather than concurrent, on purpose. Assuming every enabled scene is loaded
        /// together is the assumption that made cross-scene validation useless, because it can never
        /// report anything as unavailable. Treating them as deferred says the honest thing — they may
        /// arrive — without claiming knowledge nobody supplied.
        /// </remarks>
        private static List<ReferenceLoadSet> Infer()
        {
            var enabled = EditorBuildSettings.scenes
                .Where(s => s != null && s.enabled && !string.IsNullOrEmpty(s.path))
                .Select(s => s.path)
                .ToList();

            if (enabled.Count == 0)
                return new List<ReferenceLoadSet>();

            return new List<ReferenceLoadSet>
            {
                new ReferenceLoadSet(
                    "inferred-build-settings",
                    enabled[0],
                    concurrentScenes: null,
                    deferredScenes: enabled.Skip(1),
                    isInferred: true),
            };
        }

        /// <summary>One-line summary for the Hub's Coverage view.</summary>
        public static string Describe()
        {
            if (Sets.Count == 0)
                return "No scenes are enabled in build settings, so no load set could be determined.";

            string origin = IsInferred
                ? "inferred from build settings — author explicit sets to validate additive loading"
                : $"authored in {FilePath}";

            return $"{Sets.Count} load set{(Sets.Count == 1 ? "" : "s")}, {origin}";
        }

        // --- Serialization ----------------------------------------------------

        [Serializable]
        private sealed class LoadSetFile
        {
            public int schemaVersion;
            public LoadSetRecord[] sets;
        }

        [Serializable]
        private sealed class LoadSetRecord
        {
            public string id;
            public string entryScene;
            public string[] concurrentScenes;
            public string[] deferredScenes;

            public static LoadSetRecord From(ReferenceLoadSet set) => new LoadSetRecord
            {
                id = set.Id,
                entryScene = set.EntryScene,
                concurrentScenes = set.ConcurrentScenes.ToArray(),
                deferredScenes = set.DeferredScenes.ToArray(),
            };

            public ReferenceLoadSet ToLoadSet() =>
                new ReferenceLoadSet(id, entryScene, concurrentScenes, deferredScenes, isInferred: false);
        }
    }
}
