using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Molca.Editor.Migration;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Molca.Editor
{
    /// <summary>
    /// Inventories and transactionally migrates retained DynamicLocalization v1 payloads.
    /// </summary>
    public static class LocalizationValueMigrationService
    {
        private const int MaximumRememberedPlans = 32;
        private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(15);
        private static readonly Dictionary<string, LocalizationValueMigrationPlan> Plans = new();
        private static readonly Queue<string> PlanOrder = new();

        /// <summary>Returns a deterministic, read-only inventory of legacy values.</summary>
        /// <remarks>
        /// Every candidate is checked for prefab instances that override it. That check is not optional:
        /// migrating a source rewrites <c>translations</c> into <c>inlineSource.values</c> and empties the
        /// legacy array, so an instance overriding a row keeps overriding a field nothing reads. Before
        /// this existed the migration reported success and the difference simply disappeared.
        /// </remarks>
        public static LocalizationValueMigrationInventory Inventory(string pathFilter = null)
        {
            var candidates = new List<LocalizationValueMigrationCandidate>();
            foreach (var path in AssetDatabase.FindAssets(
                         "t:Prefab t:ScriptableObject",
                         new[] { "Assets", "Packages" })
                     .Select(AssetDatabase.GUIDToAssetPath)
                     .Where(path => path.StartsWith("Assets/", StringComparison.Ordinal) ||
                                    path.StartsWith(
                                        "Packages/com.molca.",
                                        StringComparison.Ordinal))
                     .Where(path => MatchesFilter(path, pathFilter))
                     .Where(LocalizationAuditEngine.ContainsSerializedLocalization)
                     .Distinct()
                     .OrderBy(path => path, StringComparer.Ordinal))
            {
                var main = AssetDatabase.LoadMainAssetAtPath(path);
                var targets = main switch
                {
                    GameObject gameObject => gameObject
                        .GetComponentsInChildren<MonoBehaviour>(true)
                        .Where(component => component != null)
                        .Cast<UnityEngine.Object>(),
                    ScriptableObject => AssetDatabase.LoadAllAssetsAtPath(path)
                        .OfType<ScriptableObject>()
                        .Cast<UnityEngine.Object>(),
                    _ => Enumerable.Empty<UnityEngine.Object>(),
                };
                ScanTargets(targets, path, candidates);
            }

            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (!scene.isLoaded)
                    continue;
                var path = string.IsNullOrEmpty(scene.path)
                    ? $"<unsaved>/{scene.name}"
                    : scene.path;
                if (!MatchesFilter(path, pathFilter))
                    continue;
                ScanTargets(
                    scene.GetRootGameObjects()
                        .SelectMany(root => root.GetComponentsInChildren<MonoBehaviour>(true))
                        .Where(component => component != null)
                        .Cast<UnityEngine.Object>(),
                    path,
                    candidates);
            }

            AttachInstanceOverrides(candidates);

            var ordered = candidates
                .OrderBy(candidate => candidate.AssetPath, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.ObjectId, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.PropertyPath, StringComparer.Ordinal)
                .ToArray();
            return new LocalizationValueMigrationInventory(
                ordered,
                ComputeFingerprint(ordered));
        }

        /// <summary>Tells every candidate which prefab instances override it.</summary>
        /// <remarks>
        /// One project-wide scan for the whole inventory, and only when there is something to scan for.
        /// The index reads serialized files rather than loading assets, but reading every prefab and
        /// scene is still real work to do for an inventory that turned out to be empty.
        /// </remarks>
        private static void AttachInstanceOverrides(
            IReadOnlyList<LocalizationValueMigrationCandidate> candidates)
        {
            if (candidates.Count == 0)
                return;

            var detector = LocalizedValueInstanceOverrideDetector.Build();
            foreach (var candidate in candidates)
                candidate.SetInstanceOverrides(
                    detector.Resolve(candidate.Target, candidate.PropertyPath));
        }

        /// <summary>Builds and remembers a stale-safe migration preview.</summary>
        public static LocalizationValueMigrationPlan Preview(string pathFilter = null)
        {
            var inventory = Inventory(pathFilter);
            var plan = new LocalizationValueMigrationPlan(
                pathFilter,
                inventory.Fingerprint,
                inventory.Candidates);
            foreach (var candidate in inventory.Candidates)
            {
                if (!candidate.IsWritable)
                {
                    plan.AddWarning(
                        $"Read-only legacy value requires migration in its owning package: " +
                        $"{candidate.AssetPath} · {candidate.PropertyPath}.");
                    continue;
                }

                // Refused rather than migrated-and-reported. An un-migrated value still renders what it
                // always did; a migrated one whose instance override was dropped renders the wrong string
                // immediately, and nothing in the console says so.
                if (candidate.IsBlockedByInstanceOverride)
                {
                    foreach (var blocked in candidate.InstanceOverrides.Where(o => !o.CanBeCarried))
                        plan.AddWarning(
                            $"Skipped — a prefab instance overrides this value and the override cannot " +
                            $"be carried: {candidate.AssetPath} · {candidate.PropertyPath} → " +
                            $"{blocked.ContainingAssetPath}: {blocked.Refusal}");
                    continue;
                }

                var carried = candidate.InstanceOverrides.Count;
                plan.AddChange(
                    $"{candidate.AssetPath} · {candidate.ObjectType}.{candidate.PropertyPath} " +
                    $"({candidate.SourceKind}, {candidate.RowCount} inline row(s))" +
                    (carried == 0 ? string.Empty : $", carrying {carried} instance override(s)"));
            }

            if (plan.Changes.Count == 0)
                plan.AddError(inventory.Candidates.Count == 0
                    ? "No legacy localization values were found in the selected scope."
                    : "The selected scope contains no legacy localization values that can be migrated "
                      + "without dropping a prefab-instance override.");
            Remember(plan);
            return plan;
        }

        /// <summary>Gets an unexpired preview by opaque id.</summary>
        public static bool TryGetPlan(
            string planId,
            out LocalizationValueMigrationPlan plan)
        {
            plan = null;
            if (string.IsNullOrWhiteSpace(planId) || !Plans.TryGetValue(planId, out var found))
                return false;
            if (DateTime.UtcNow - found.CreatedAtUtc > PlanLifetime)
            {
                Plans.Remove(planId);
                return false;
            }
            plan = found;
            return true;
        }

        /// <summary>Executes one preview as a single Unity Undo transaction.</summary>
        public static LocalizationValueMigrationResult Execute(
            LocalizationValueMigrationPlan plan)
        {
            if (plan == null)
                return LocalizationValueMigrationResult.Failure("A migration plan is required.");
            if (!plan.IsExecutable)
                return LocalizationValueMigrationResult.Failure(
                    "The migration preview has blocking errors.");
            if (DateTime.UtcNow - plan.CreatedAtUtc > PlanLifetime)
                return LocalizationValueMigrationResult.Stale(
                    "The migration preview expired. Preview again.");

            var current = Inventory(plan.PathFilter);
            if (!string.Equals(
                    current.Fingerprint,
                    plan.SourceFingerprint,
                    StringComparison.Ordinal))
                return LocalizationValueMigrationResult.Stale(
                    "Localization values changed after preview. Preview again.");

            var currentById = current.Candidates.ToDictionary(
                candidate => candidate.StableId,
                StringComparer.Ordinal);
            var selected = plan.Candidates
                .Where(candidate => candidate.IsWritable && !candidate.IsBlockedByInstanceOverride)
                .Select(candidate => currentById.TryGetValue(candidate.StableId, out var found)
                    ? found
                    : null)
                .ToArray();
            if (selected.Any(candidate => candidate == null))
                return LocalizationValueMigrationResult.Stale(
                    "A previewed localization value no longer exists. Preview again.");

            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Migrate LocalizedValue schema");
            try
            {
                foreach (var candidate in selected)
                {
                    Undo.RecordObject(candidate.Target, "Migrate LocalizedValue schema");
                    var serialized = new SerializedObject(candidate.Target);
                    var property = serialized.FindProperty(candidate.PropertyPath);
                    if (!LocalizedValueSerializedUtility.TryDescribe(
                            property,
                            out var descriptor) ||
                        !descriptor.IsLegacy)
                        throw new InvalidOperationException(
                            $"Legacy value changed at {candidate.AssetPath} · " +
                            candidate.PropertyPath);
                    LocalizedValueSerializedUtility.MigrateLegacy(property);
                    if (!serialized.ApplyModifiedProperties())
                        throw new InvalidOperationException(
                            $"Unity did not apply the migration at {candidate.AssetPath} · " +
                            candidate.PropertyPath);
                    EditorUtility.SetDirty(candidate.Target);
                    if (candidate.Target is Component component &&
                        component.gameObject.scene.IsValid() &&
                        component.gameObject.scene.isLoaded)
                        EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
                }

                Undo.CollapseUndoOperations(undoGroup);
                AssetDatabase.SaveAssets();

                // A second phase, after every source has been written: an override is repointed at
                // schema-v2 fields that only carry a value once the source has been migrated into them.
                CarryInstanceOverrides(selected);
                AssetDatabase.SaveAssets();

                var postInventory = Inventory(plan.PathFilter);
                var remainingIds = postInventory.Candidates
                    .Select(candidate => candidate.StableId)
                    .ToHashSet(StringComparer.Ordinal);
                var notMigrated = selected
                    .Where(candidate => remainingIds.Contains(candidate.StableId))
                    .ToArray();
                if (notMigrated.Length > 0)
                    throw new InvalidOperationException(
                        $"Post-verification found {notMigrated.Length} value(s) still on the legacy schema.");

                var postAudit = LocalizationAuditEngine.Audit(
                    LocalizationAuditRequest.CreateDoctorRequest());
                return LocalizationValueMigrationResult.Success(
                    selected.Length,
                    postInventory,
                    postAudit);
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                AssetDatabase.SaveAssets();
                return LocalizationValueMigrationResult.Failure(
                    $"Migration rolled back: {exception.Message}");
            }
        }

        /// <summary>Rewrites every carried override from the legacy fields onto their schema-v2 twins.</summary>
        /// <remarks>
        /// Throws on failure so the surrounding <c>try</c> rolls the whole migration back. Half a
        /// migration — sources on the new schema, instances still overriding the old one — is precisely
        /// the state this work exists to make impossible.
        /// </remarks>
        private static void CarryInstanceOverrides(
            IReadOnlyList<LocalizationValueMigrationCandidate> migrated)
        {
            var rewrites = new List<PrefabInstanceRewrite>();

            foreach (var candidate in migrated)
            {
                foreach (var entry in candidate.InstanceOverrides.Where(o => o.CanBeCarried))
                {
                    var set = entry.Translated
                        .Select(t => (candidate.Target, t.PropertyPath, t.Value))
                        .ToList();

                    // The legacy modifications name fields that still exist but are now empty, so they
                    // cannot be recognized by a null target the way a removed component's are. Matching
                    // on this candidate's own target and its own recorded paths keeps the removal from
                    // reaching any other value on the same object.
                    var legacyPaths = new HashSet<string>(entry.LegacyPropertyPaths, StringComparer.Ordinal);
                    rewrites.Add(new PrefabInstanceRewrite(
                        entry.ContainingAssetPath,
                        entry.InstanceFileId,
                        set,
                        modification => ReferenceEquals(modification.target, candidate.Target)
                                        && legacyPaths.Contains(modification.propertyPath)));
                }
            }

            if (rewrites.Count == 0)
                return;

            PrefabInstanceOverrideWriter.Apply(rewrites, out _, out var failures);
            if (failures.Count > 0)
                throw new InvalidOperationException(
                    $"{failures.Count} prefab-instance override(s) could not be carried onto the new " +
                    $"schema: {string.Join("; ", failures)}");
        }

        private static void ScanTargets(
            IEnumerable<UnityEngine.Object> targets,
            string assetPath,
            ICollection<LocalizationValueMigrationCandidate> candidates)
        {
            foreach (var target in targets)
            {
                var serialized = new SerializedObject(target);
                var property = serialized.GetIterator();
                var enterChildren = true;
                while (property.Next(enterChildren))
                {
                    enterChildren = true;
                    if (!LocalizedValueSerializedUtility.TryDescribe(
                            property,
                            out var descriptor))
                        continue;
                    enterChildren = false;
                    if (!descriptor.IsLegacy)
                        continue;

                    var objectId = GlobalObjectId.GetGlobalObjectIdSlow(target).ToString();
                    if (string.IsNullOrEmpty(objectId))
                        objectId = $"instance:{target.GetInstanceID()}";
                    var writable = assetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                                   assetPath.StartsWith("<unsaved>/", StringComparison.Ordinal);
                    if (writable &&
                        assetPath.StartsWith("Assets/", StringComparison.Ordinal) &&
                        !AssetDatabase.IsOpenForEdit(assetPath))
                        writable = false;
                    candidates.Add(new LocalizationValueMigrationCandidate(
                        target,
                        assetPath,
                        objectId,
                        target.GetType().FullName,
                        property.propertyPath,
                        descriptor.SourceKind,
                        descriptor.Rows?.arraySize ?? 0,
                        writable,
                        CapturePayload(descriptor)));
                }
            }
        }

        private static string CapturePayload(LocalizedValueSerializedDescriptor descriptor)
        {
            var builder = new StringBuilder()
                .Append(descriptor.SchemaVersion.intValue).Append('|')
                .Append((int)descriptor.SourceKind).Append('|');
            if (descriptor.Rows != null)
                for (var index = 0; index < descriptor.Rows.arraySize; index++)
                {
                    var row = descriptor.Rows.GetArrayElementAtIndex(index);
                    builder.Append(row.FindPropertyRelative(descriptor.CodeField)?.stringValue)
                        .Append('=')
                        .Append(row.FindPropertyRelative(descriptor.ValueField)?.stringValue)
                        .Append('|');
                }
            if (descriptor.CatalogReference != null)
            {
                if (descriptor.CatalogReference.boxedValue is
                    UnityEngine.Localization.LocalizedString reference)
                    builder.Append(reference.TableReference.ToString()).Append('/')
                        .Append(reference.TableEntryReference.ToString());
                else
                    builder.Append(descriptor.CatalogReference.boxedValue?.ToString());
            }
            return builder.ToString();
        }

        private static string ComputeFingerprint(
            IEnumerable<LocalizationValueMigrationCandidate> candidates)
        {
            var canonical = string.Join(
                "\n",
                candidates.Select(candidate =>
                    $"{candidate.StableId}|{candidate.Payload}"));
            return Hash128.Compute(canonical).ToString();
        }

        private static bool MatchesFilter(string path, string filter) =>
            string.IsNullOrWhiteSpace(filter) ||
            path.IndexOf(filter.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;

        private static void Remember(LocalizationValueMigrationPlan plan)
        {
            Plans[plan.PlanId] = plan;
            PlanOrder.Enqueue(plan.PlanId);
            while (PlanOrder.Count > MaximumRememberedPlans)
                Plans.Remove(PlanOrder.Dequeue());
        }
    }

    /// <summary>One stable legacy value locator.</summary>
    public sealed class LocalizationValueMigrationCandidate
    {
        internal LocalizationValueMigrationCandidate(
            UnityEngine.Object target,
            string assetPath,
            string objectId,
            string objectType,
            string propertyPath,
            Molca.Localization.LocalizedValueSourceKind sourceKind,
            int rowCount,
            bool isWritable,
            string payload)
        {
            Target = target;
            AssetPath = assetPath;
            ObjectId = objectId;
            ObjectType = objectType;
            PropertyPath = propertyPath;
            SourceKind = sourceKind;
            RowCount = rowCount;
            IsWritable = isWritable;
            Payload = payload;
        }

        internal UnityEngine.Object Target { get; }
        internal string Payload { get; }
        public string AssetPath { get; }
        public string ObjectId { get; }
        public string ObjectType { get; }
        public string PropertyPath { get; }
        public Molca.Localization.LocalizedValueSourceKind SourceKind { get; }
        public int RowCount { get; }
        public bool IsWritable { get; }
        public string StableId => $"{AssetPath}|{ObjectId}|{PropertyPath}";

        /// <summary>Prefab instances that override this value.</summary>
        /// <remarks>
        /// Empty for the overwhelming majority of candidates. A non-empty list is the difference between
        /// a migration that carries what the author authored and one that quietly replaces it with the
        /// source's value.
        /// </remarks>
        public IReadOnlyList<LocalizedValueInstanceOverride> InstanceOverrides { get; private set; } =
            Array.Empty<LocalizedValueInstanceOverride>();

        /// <summary>Whether an override on this value stops it being migrated.</summary>
        public bool IsBlockedByInstanceOverride =>
            InstanceOverrides.Any(instanceOverride => !instanceOverride.CanBeCarried);

        internal void SetInstanceOverrides(IReadOnlyList<LocalizedValueInstanceOverride> overrides) =>
            InstanceOverrides = overrides ?? Array.Empty<LocalizedValueInstanceOverride>();
    }

    /// <summary>Immutable migration inventory and its source fingerprint.</summary>
    public sealed class LocalizationValueMigrationInventory
    {
        internal LocalizationValueMigrationInventory(
            IReadOnlyList<LocalizationValueMigrationCandidate> candidates,
            string fingerprint)
        {
            Candidates = candidates;
            Fingerprint = fingerprint;
        }

        public IReadOnlyList<LocalizationValueMigrationCandidate> Candidates { get; }
        public string Fingerprint { get; }
    }

    /// <summary>Preview required before schema migration.</summary>
    public sealed class LocalizationValueMigrationPlan
    {
        private readonly List<string> _changes = new();
        private readonly List<string> _warnings = new();
        private readonly List<string> _errors = new();

        internal LocalizationValueMigrationPlan(
            string pathFilter,
            string sourceFingerprint,
            IReadOnlyList<LocalizationValueMigrationCandidate> candidates)
        {
            PlanId = Guid.NewGuid().ToString("N");
            CreatedAtUtc = DateTime.UtcNow;
            PathFilter = pathFilter;
            SourceFingerprint = sourceFingerprint;
            Candidates = candidates;
        }

        public string PlanId { get; }
        public DateTime CreatedAtUtc { get; }
        public string PathFilter { get; }
        public string SourceFingerprint { get; }
        public IReadOnlyList<LocalizationValueMigrationCandidate> Candidates { get; }
        public IReadOnlyList<string> Changes => _changes;
        public IReadOnlyList<string> Warnings => _warnings;
        public IReadOnlyList<string> Errors => _errors;
        public bool IsExecutable => _errors.Count == 0 && _changes.Count > 0;
        internal void AddChange(string value) => _changes.Add(value);
        internal void AddWarning(string value) => _warnings.Add(value);
        internal void AddError(string value) => _errors.Add(value);
    }

    /// <summary>Verified migration outcome.</summary>
    public sealed class LocalizationValueMigrationResult
    {
        private LocalizationValueMigrationResult() { }
        public bool Succeeded { get; private set; }
        public bool WasStale { get; private set; }
        public string Error { get; private set; }
        public int ChangedCount { get; private set; }
        public LocalizationValueMigrationInventory PostInventory { get; private set; }
        public LocalizationAuditSnapshot PostAudit { get; private set; }

        internal static LocalizationValueMigrationResult Success(
            int changedCount,
            LocalizationValueMigrationInventory postInventory,
            LocalizationAuditSnapshot postAudit) =>
            new()
            {
                Succeeded = true,
                ChangedCount = changedCount,
                PostInventory = postInventory,
                PostAudit = postAudit,
            };

        internal static LocalizationValueMigrationResult Failure(string error) =>
            new() { Error = error };

        internal static LocalizationValueMigrationResult Stale(string error) =>
            new() { Error = error, WasStale = true };
    }
}
