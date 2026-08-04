using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Molca.ContentPackage;
using Molca.ContentPackage.Release;

namespace Molca.ContentPackage.Editor
{
    /// <summary>The result of one editing operation.</summary>
    public sealed class ContentEditResult
    {
        /// <summary>True when the asset changed.</summary>
        public bool Changed { get; private set; }

        /// <summary>Why nothing changed, or what changed. Never empty.</summary>
        public string Message { get; private set; } = "";

        /// <summary>A short description of the prior value, for a remediation report's before/after.</summary>
        public string Before { get; private set; } = "";

        /// <summary>A short description of the new value.</summary>
        public string After { get; private set; } = "";

        /// <summary>Builds a "changed" result.</summary>
        /// <param name="message">What changed.</param>
        /// <param name="before">Prior value, for a report.</param>
        /// <param name="after">New value, for a report.</param>
        public static ContentEditResult Ok(string message, string before = "", string after = "") =>
            new ContentEditResult { Changed = true, Message = message, Before = before, After = after };

        /// <summary>Builds a "nothing changed" result carrying the reason.</summary>
        /// <param name="message">Why nothing changed.</param>
        public static ContentEditResult NoChange(string message) =>
            new ContentEditResult { Changed = false, Message = message ?? "" };
    }

    /// <summary>
    /// The one place <see cref="ContentPackageSettings"/> is written.
    /// </summary>
    /// <remarks>
    /// Extracted from <c>ContentPackageSettingsEditor</c>, which owned every write through its own
    /// <see cref="SerializedObject"/>. That made the inspector the only way to change a setting, so
    /// automation, the MCP tools, and the remediation fixes in
    /// <c>docs/internal/UNIFIED_REMEDIATION_DESIGN.md</c> §5.7 had nowhere to route a mutation — and
    /// a fix that depends on a window being open is not a fix.
    ///
    /// Every operation here:
    ///
    /// <list type="bullet">
    /// <item>works on the asset, never on a live inspector;</item>
    /// <item>registers one Undo entry, so a caller can group a batch and revert it as a unit;</item>
    /// <item>refuses assets in a read-only zone, rather than writing something an upgrade discards;</item>
    /// <item>reports what it did — or why it declined — instead of returning void.</item>
    /// </list>
    ///
    /// It deliberately owns no judgment. Operations that would resolve an ambiguity (which duplicate
    /// package id is the real one, which dependency edge is wrong) are absent, because the caller
    /// that wants them would be guessing and the guess belongs to a human. For the same reason a
    /// setter accepts a value that merely <em>validates</em> badly — a non-semantic version, an id the
    /// server would reject — and leaves saying so to <see cref="ContentValidation"/>. It refuses only
    /// what would make the definition set incoherent: a duplicate id, a self-dependency, a trusted key
    /// that cannot verify.
    ///
    /// Split across two files: this one owns the structural operations and the release-protocol
    /// configuration; <c>ContentPackageEditingService.Fields.cs</c> owns the per-field setters the
    /// authoring surfaces bind to.
    /// </remarks>
    public sealed partial class ContentPackageEditingService
    {
        private readonly ContentPackageSettings _settings;

        /// <summary>Builds a service over one settings asset.</summary>
        /// <param name="settings">The asset to edit. Required.</param>
        /// <exception cref="ArgumentNullException">The asset is null.</exception>
        public ContentPackageEditingService(ContentPackageSettings settings)
        {
            _settings = settings ? settings : throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>The asset this service writes.</summary>
        public ContentPackageSettings Settings => _settings;

        /// <summary>
        /// Why this asset cannot be edited, or null when it can.
        /// </summary>
        /// <remarks>
        /// A settings asset inside a package is not project-owned: an upgrade replaces it, so a write
        /// here is silently reverted later. That matters most for the release-protocol fields, which
        /// carry the trusted signing keys — a project would revert to whatever the package shipped
        /// without anything saying so.
        /// </remarks>
        public string ReadOnlyReason()
        {
            string path = AssetDatabase.GetAssetPath(_settings);
            if (string.IsNullOrEmpty(path)) return null; // in-memory instance, e.g. a test fixture

            string normalized = path.Replace('\\', '/');
            if (normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return $"'{path}' lives inside a package. Package assets are replaced on upgrade, so a " +
                       "project's content settings must live under Assets/.";
            if (normalized.StartsWith("Assets/_MolcaSDK/", StringComparison.OrdinalIgnoreCase))
                return $"'{path}' is in the read-only SDK layer.";
            return null;
        }

        // ── Package definitions ──────────────────────────────────────────────

        /// <summary>Adds a package with a unique placeholder id.</summary>
        /// <param name="packageId">The id to use, or null to generate one.</param>
        /// <returns>The result; <see cref="ContentEditResult.After"/> carries the id used.</returns>
        public ContentEditResult AddPackage(string packageId = null)
        {
            string reason = ReadOnlyReason();
            if (reason != null) return ContentEditResult.NoChange(reason);

            string id = packageId;
            if (string.IsNullOrWhiteSpace(id))
            {
                id = "new-package";
                for (int n = 1; _settings.GetPackageConfig(id) != null; n++) id = $"new-package-{n}";
            }
            else if (_settings.GetPackageConfig(id) != null)
            {
                return ContentEditResult.NoChange($"A package with id '{id}' already exists.");
            }

            Record("Add Content Package");
            _settings.packageConfigs.Add(new ContentPackageSettings.PackageConfig
            {
                packageId = id,
                displayName = "",
                isVisible = true,
                isRequired = false,
                addressableLabels = Array.Empty<string>(),
                dependencies = Array.Empty<ContentPackageSettings.PackageDependency>(),
                metadata = new ContentPackageSettings.PackageMetadata { version = "1.0.0" },
            });
            Commit();
            return ContentEditResult.Ok($"Added package '{id}'.", "", id);
        }

        /// <summary>Removes a package by id.</summary>
        /// <param name="packageId">The package to remove.</param>
        public ContentEditResult RemovePackage(string packageId)
        {
            string reason = ReadOnlyReason();
            if (reason != null) return ContentEditResult.NoChange(reason);

            int index = _settings.packageConfigs.FindIndex(config => config?.packageId == packageId);
            if (index < 0) return ContentEditResult.NoChange($"No package '{packageId}'.");

            // Named rather than silently orphaned: removing a package that others depend on leaves
            // `dependency_missing` findings the caller should know about before it happens.
            var dependents = _settings.packageConfigs
                .Where(config => config?.dependencies != null &&
                                 config.dependencies.Any(d => d?.packageId == packageId))
                .Select(config => config.packageId)
                .ToList();

            Record("Remove Content Package");
            _settings.packageConfigs.RemoveAt(index);
            Commit();

            string note = dependents.Count > 0
                ? $" Still referenced by: {string.Join(", ", dependents)}."
                : "";
            return ContentEditResult.Ok($"Removed package '{packageId}'.{note}", packageId, "");
        }

        /// <summary>Sets a package's display name.</summary>
        /// <param name="packageId">The package to change.</param>
        /// <param name="displayName">The new display name.</param>
        public ContentEditResult SetDisplayName(string packageId, string displayName)
        {
            return Mutate(packageId, "Set Display Name", config =>
            {
                if (string.Equals(config.displayName, displayName, StringComparison.Ordinal))
                    return ContentEditResult.NoChange("Display name is already that.");
                string before = config.displayName ?? "";
                config.displayName = displayName ?? "";
                return ContentEditResult.Ok($"Display name set on '{packageId}'.", before, config.displayName);
            });
        }

        /// <summary>
        /// Derives a missing display name from the package id.
        /// </summary>
        /// <remarks>
        /// Remediation `package_display_name_missing`. Only fills an <em>empty</em> name — overwriting
        /// an authored one would discard a human's wording to satisfy a warning.
        /// </remarks>
        /// <param name="packageId">The package to change.</param>
        public ContentEditResult DeriveDisplayNameFromId(string packageId)
        {
            return Mutate(packageId, "Derive Display Name", config =>
            {
                if (!string.IsNullOrWhiteSpace(config.displayName))
                    return ContentEditResult.NoChange("Display name is already set.");
                if (string.IsNullOrWhiteSpace(config.packageId))
                    return ContentEditResult.NoChange("Package has no id to derive from.");

                string derived = Titleize(config.packageId);
                config.displayName = derived;
                return ContentEditResult.Ok($"Derived display name for '{packageId}'.", "", derived);
            });
        }

        /// <summary>
        /// Removes duplicate label entries, keeping the first of each.
        /// </summary>
        /// <remarks>Remediation `label_duplicate`. Ordinal comparison: Addressables labels are case sensitive.</remarks>
        /// <param name="packageId">The package to change.</param>
        public ContentEditResult DedupeLabels(string packageId)
        {
            return Mutate(packageId, "Dedupe Labels", config =>
            {
                var labels = config.addressableLabels ?? Array.Empty<string>();
                var kept = new List<string>(labels.Length);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string label in labels)
                    if (seen.Add(label)) kept.Add(label);

                if (kept.Count == labels.Length) return ContentEditResult.NoChange("No duplicate labels.");
                string before = string.Join(", ", labels);
                config.addressableLabels = kept.ToArray();
                return ContentEditResult.Ok(
                    $"Removed {labels.Length - kept.Count} duplicate label(s) from '{packageId}'.",
                    before, string.Join(", ", kept));
            });
        }

        /// <summary>Removes empty label entries.</summary>
        /// <remarks>Remediation `label_empty`.</remarks>
        /// <param name="packageId">The package to change.</param>
        public ContentEditResult RemoveEmptyLabels(string packageId)
        {
            return Mutate(packageId, "Remove Empty Labels", config =>
            {
                var labels = config.addressableLabels ?? Array.Empty<string>();
                var kept = labels.Where(label => !string.IsNullOrWhiteSpace(label)).ToArray();

                if (kept.Length == labels.Length) return ContentEditResult.NoChange("No empty labels.");
                config.addressableLabels = kept;
                return ContentEditResult.Ok(
                    $"Removed {labels.Length - kept.Length} empty label entr(ies) from '{packageId}'.",
                    string.Join(", ", labels), string.Join(", ", kept));
            });
        }

        /// <summary>Replaces a package's labels.</summary>
        /// <param name="packageId">The package to change.</param>
        /// <param name="labels">The labels to set.</param>
        public ContentEditResult SetLabels(string packageId, IEnumerable<string> labels)
        {
            return Mutate(packageId, "Set Labels", config =>
            {
                string before = string.Join(", ", config.addressableLabels ?? Array.Empty<string>());
                config.addressableLabels = (labels ?? Enumerable.Empty<string>()).ToArray();
                string after = string.Join(", ", config.addressableLabels);
                return before == after
                    ? ContentEditResult.NoChange("Labels are already that.")
                    : ContentEditResult.Ok($"Labels set on '{packageId}'.", before, after);
            });
        }

        /// <summary>Removes duplicate dependency entries, keeping the first of each.</summary>
        /// <remarks>Remediation `dependency_duplicate`.</remarks>
        /// <param name="packageId">The package to change.</param>
        public ContentEditResult DedupeDependencies(string packageId)
        {
            return Mutate(packageId, "Dedupe Dependencies", config =>
            {
                var dependencies = config.dependencies ?? Array.Empty<ContentPackageSettings.PackageDependency>();
                var kept = new List<ContentPackageSettings.PackageDependency>(dependencies.Length);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var dependency in dependencies)
                    if (dependency != null && seen.Add(dependency.packageId ?? "")) kept.Add(dependency);

                if (kept.Count == dependencies.Length)
                    return ContentEditResult.NoChange("No duplicate dependencies.");

                string before = string.Join(", ", dependencies.Select(d => d?.packageId));
                config.dependencies = kept.ToArray();
                return ContentEditResult.Ok(
                    $"Removed {dependencies.Length - kept.Count} duplicate dependenc(ies) from '{packageId}'.",
                    before, string.Join(", ", kept.Select(d => d.packageId)));
            });
        }

        /// <summary>Removes a package's dependency on itself.</summary>
        /// <remarks>
        /// Remediation `dependency_self`. Mechanically unambiguous, but it usually means the author
        /// meant to name a different package — so it is reported as a change worth looking at rather
        /// than treated as routine tidying.
        /// </remarks>
        /// <param name="packageId">The package to change.</param>
        public ContentEditResult RemoveSelfDependency(string packageId)
        {
            return Mutate(packageId, "Remove Self Dependency", config =>
            {
                var dependencies = config.dependencies ?? Array.Empty<ContentPackageSettings.PackageDependency>();
                var kept = dependencies
                    .Where(d => d != null && !string.Equals(d.packageId, config.packageId, StringComparison.Ordinal))
                    .ToArray();

                if (kept.Length == dependencies.Length)
                    return ContentEditResult.NoChange("No self-dependency.");

                config.dependencies = kept;
                return ContentEditResult.Ok(
                    $"Removed self-dependency from '{packageId}'. Check whether another package was meant.",
                    packageId, "");
            });
        }

        /// <summary>Sets one dependency's target package id.</summary>
        /// <param name="packageId">The depending package.</param>
        /// <param name="index">Index into that package's dependency list.</param>
        /// <param name="targetPackageId">The id to point at.</param>
        public ContentEditResult SetDependency(string packageId, int index, string targetPackageId)
        {
            return Mutate(packageId, "Set Dependency", config =>
            {
                var dependencies = config.dependencies ?? Array.Empty<ContentPackageSettings.PackageDependency>();
                if (index < 0 || index >= dependencies.Length)
                    return ContentEditResult.NoChange($"No dependency at index {index}.");

                dependencies[index] ??= new ContentPackageSettings.PackageDependency();
                string before = dependencies[index].packageId ?? "";
                dependencies[index].packageId = targetPackageId ?? "";
                return ContentEditResult.Ok($"Dependency set on '{packageId}'.", before, targetPackageId ?? "");
            });
        }

        // ── Release protocol configuration ───────────────────────────────────

        /// <summary>Enables or disables the release protocol.</summary>
        /// <param name="enabled">Whether content resolves through the release protocol.</param>
        public ContentEditResult SetReleaseProtocolEnabled(bool enabled) =>
            SetSerializedField("_enableReleaseProtocol", property => property.boolValue,
                property => property.boolValue = enabled,
                value => value.ToString(), "Set Release Protocol Enabled");

        /// <summary>Sets the network catalog service id for the content host.</summary>
        /// <param name="serviceId">The service id.</param>
        public ContentEditResult SetContentServiceId(string serviceId) =>
            SetSerializedField("_contentServiceId", property => property.stringValue,
                property => property.stringValue = serviceId ?? "",
                value => value?.ToString() ?? "", "Set Content Service Id");

        /// <summary>Sets the content API path prefix.</summary>
        /// <param name="pathPrefix">The path prefix, e.g. <c>/content/v1</c>.</param>
        public ContentEditResult SetContentPathPrefix(string pathPrefix) =>
            SetSerializedField("_contentPathPrefix", property => property.stringValue,
                property => property.stringValue = pathPrefix ?? "",
                value => value?.ToString() ?? "", "Set Content Path Prefix");

        /// <summary>
        /// Replaces the trusted release signing keys.
        /// </summary>
        /// <remarks>
        /// The one write here that is security-relevant, so incomplete entries are refused rather
        /// than stored. A half-filled key does not fail at authoring time — it fails at runtime, as
        /// a signature error indistinguishable from tampering.
        /// </remarks>
        /// <param name="keys">The keys to trust. An empty list means nothing can verify.</param>
        public ContentEditResult SetTrustedReleaseKeys(IReadOnlyList<ReleaseTrustedKey> keys)
        {
            string reason = ReadOnlyReason();
            if (reason != null) return ContentEditResult.NoChange(reason);

            var incoming = keys ?? Array.Empty<ReleaseTrustedKey>();
            foreach (var key in incoming)
            {
                if (key == null || !key.IsComplete)
                    return ContentEditResult.NoChange(
                        $"Key '{key?.KeyId ?? "(null)"}' is missing an id, modulus, or exponent. " +
                        "An incomplete key fails at runtime as a signature error, not here.");
            }

            var duplicate = incoming.GroupBy(key => key.KeyId, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
                return ContentEditResult.NoChange($"Key id '{duplicate.Key}' appears more than once.");

            var serialized = new SerializedObject(_settings);
            var property = serialized.FindProperty("_trustedReleaseKeys");
            if (property == null) return ContentEditResult.NoChange("This settings asset has no trusted key list.");

            string before = string.Join(", ", Enumerable.Range(0, property.arraySize)
                .Select(i => property.GetArrayElementAtIndex(i).FindPropertyRelative("KeyId").stringValue));

            Record("Set Trusted Release Keys");
            property.arraySize = incoming.Count;
            for (int i = 0; i < incoming.Count; i++)
            {
                var element = property.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("KeyId").stringValue = incoming[i].KeyId;
                element.FindPropertyRelative("ModulusBase64").stringValue = incoming[i].ModulusBase64;
                element.FindPropertyRelative("ExponentBase64").stringValue = incoming[i].ExponentBase64;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Commit();

            string after = string.Join(", ", incoming.Select(key => key.KeyId));
            return ContentEditResult.Ok(
                incoming.Count == 0
                    ? "Cleared trusted release keys. No release will verify until one is added."
                    : $"Set {incoming.Count} trusted release key(s).",
                before, after);
        }

        // ── Validation passthrough ───────────────────────────────────────────

        /// <summary>
        /// Validates the current definitions through the one shared engine.
        /// </summary>
        /// <remarks>
        /// Exposed here so a caller never has a reason to build its own check. Settings-level only —
        /// findings that need a build graph are out of scope by design, not by omission.
        /// </remarks>
        public ContentValidationReport Validate() =>
            ContentValidation.ValidateSettings(_settings.packageConfigs);

        // ── Internals ────────────────────────────────────────────────────────

        private ContentEditResult Mutate(
            string packageId, string undoLabel, Func<ContentPackageSettings.PackageConfig, ContentEditResult> change)
        {
            string reason = ReadOnlyReason();
            if (reason != null) return ContentEditResult.NoChange(reason);

            var config = _settings.packageConfigs.FirstOrDefault(entry => entry?.packageId == packageId);
            if (config == null) return ContentEditResult.NoChange($"No package '{packageId}'.");

            // Recorded before the delegate runs, because Undo must capture the pre-change state; a
            // no-op still leaves a harmless empty entry, which beats a change with no undo.
            Record(undoLabel);
            var result = change(config);
            if (result.Changed) Commit();
            return result;
        }

        private ContentEditResult SetSerializedField(
            string fieldName,
            Func<SerializedProperty, object> read,
            Action<SerializedProperty> write,
            Func<object, string> describe,
            string undoLabel)
        {
            string reason = ReadOnlyReason();
            if (reason != null) return ContentEditResult.NoChange(reason);

            var serialized = new SerializedObject(_settings);
            var property = serialized.FindProperty(fieldName);
            if (property == null) return ContentEditResult.NoChange($"No serialized field '{fieldName}'.");

            string before = describe(read(property));
            Record(undoLabel);
            write(property);
            string after = describe(read(property));
            if (before == after) return ContentEditResult.NoChange($"'{fieldName}' is already {after}.");

            serialized.ApplyModifiedPropertiesWithoutUndo();
            Commit();
            return ContentEditResult.Ok($"Set {fieldName} to {after}.", before, after);
        }

        private void Record(string label) => Undo.RecordObject(_settings, label);

        private void Commit()
        {
            EditorUtility.SetDirty(_settings);
            // Not saved here. A caller applying a batch saves once at the end, and a fix pass wants
            // the whole group revertible as a unit -- writing to disk per operation would defeat
            // both and make an interrupted batch half-persisted.
        }

        /// <summary>Turns <c>exhibit.winter-hall</c> into <c>Exhibit Winter Hall</c>.</summary>
        private static string Titleize(string packageId)
        {
            var words = packageId
                .Split(new[] { '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(word => word.Length == 1
                    ? word.ToUpperInvariant()
                    : char.ToUpperInvariant(word[0]) + word.Substring(1));
            return string.Join(" ", words);
        }
    }
}
