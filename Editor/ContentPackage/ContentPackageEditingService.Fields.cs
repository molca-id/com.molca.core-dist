using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Molca.ContentPackage.Editor
{
    /// <summary>
    /// The per-field setters behind the authoring controls.
    /// </summary>
    /// <remarks>
    /// These exist because the Content workspace could show every authored value and change almost none
    /// of them: anything past add-and-delete meant selecting the settings asset and editing it in the
    /// Inspector, and the two MCP authoring tools wrote <see cref="ContentPackageSettings.PackageConfig"/>
    /// fields directly — bypassing <see cref="ContentPackageEditingService.ReadOnlyReason"/>, so an agent
    /// could write a settings asset that an upgrade silently discards.
    /// <para>
    /// One setter per authored field, grouped so one form section is one Undo step, and each passes its
    /// siblings through unchanged. A caller applying several saves once at the end; nothing here writes
    /// to disk.
    /// </para>
    /// <para>
    /// <b>Materialising metadata is reported, not silent.</b>
    /// <see cref="ContentPackageSettings.PackageMetadata"/> can be null on a
    /// <see cref="ContentPackageSettings.PackageConfig"/> built in code, and its constructor defaults
    /// <see cref="ContentPackageSettings.PackageMetadata.version"/> to <c>1.0.0</c>. So setting a
    /// description on such a config also gives it a version, which turns a blocking
    /// <c>package_version_missing</c> finding into a passing one. That is a real change to what a
    /// release would contain, so the result says it happened.
    /// </para>
    /// <para>
    /// That path is narrower than it looks, and the reason is worth writing down: Unity does not
    /// serialize null for a nested <c>[Serializable]</c> class, so the field is materialised for you
    /// the first time the asset is serialized — which <see cref="UnityEditor.Undo.RecordObject"/>
    /// alone is enough to trigger, before any setter here runs. A config loaded from an asset therefore
    /// never has a null block. The guard covers the window before that: a freshly constructed config
    /// that no serialization has touched yet.
    /// </para>
    /// </remarks>
    public sealed partial class ContentPackageEditingService
    {
        // ── Identity ─────────────────────────────────────────────────────────

        /// <summary>
        /// Changes a package's id, retargeting the packages that depend on it.
        /// </summary>
        /// <param name="packageId">The package to rename.</param>
        /// <param name="newPackageId">The id to give it.</param>
        /// <param name="retargetDependents">
        /// Whether dependencies naming the old id are moved to the new one. Leaving them behind turns
        /// every dependent into a <c>dependency_missing</c> error on a package the author did not touch,
        /// so this defaults to on.
        /// </param>
        /// <returns>The result; the message names the dependents that moved.</returns>
        /// <remarks>
        /// A package id is a key, not a caption. Dependencies reference it by string, a workspace's
        /// selection is keyed on it, and an installed <c>PackageState</c> on a player's device is keyed
        /// on it too — so a rename orphans whatever players already installed under the old id, which
        /// they will re-download under the new one. That is not a reason to refuse, but it is a reason
        /// this is a named operation rather than an editable text field.
        /// </remarks>
        public ContentEditResult RenamePackage(
            string packageId, string newPackageId, bool retargetDependents = true)
        {
            string reason = ReadOnlyReason();
            if (reason != null) return ContentEditResult.NoChange(reason);

            var config = Find(packageId);
            if (config == null) return ContentEditResult.NoChange($"No package '{packageId}'.");

            string next = newPackageId?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(next))
                return ContentEditResult.NoChange("A package id cannot be empty.");
            if (string.Equals(next, config.packageId, StringComparison.Ordinal))
                return ContentEditResult.NoChange("The id is already that.");

            if (_settings.packageConfigs.Any(entry =>
                    entry != null && entry != config &&
                    string.Equals(entry.packageId, next, StringComparison.Ordinal)))
            {
                return ContentEditResult.NoChange($"A package with id '{next}' already exists.");
            }

            var dependents = new List<string>();
            Record("Rename Content Package");

            if (retargetDependents)
            {
                foreach (var entry in _settings.packageConfigs)
                {
                    if (entry?.dependencies == null) continue;

                    bool touched = false;
                    foreach (var dependency in entry.dependencies)
                    {
                        if (dependency == null) continue;
                        if (!string.Equals(dependency.packageId, packageId, StringComparison.Ordinal)) continue;

                        dependency.packageId = next;
                        touched = true;
                    }

                    if (touched) dependents.Add(entry.packageId);
                }
            }

            config.packageId = next;
            Commit();

            string note = dependents.Count > 0
                ? $" Retargeted {dependents.Count} dependent(s): {string.Join(", ", dependents)}."
                : "";
            return ContentEditResult.Ok(
                $"Renamed '{packageId}' to '{next}'.{note} Content already installed under the old id is " +
                "orphaned on devices that have it.",
                packageId, next);
        }

        /// <summary>Sets a package's description.</summary>
        /// <param name="packageId">The package to change.</param>
        /// <param name="description">The new description.</param>
        /// <returns>The result.</returns>
        public ContentEditResult SetDescription(string packageId, string description) =>
            MutateMetadata(packageId, "Set Description", (metadata, note) =>
            {
                string value = description ?? "";
                if (string.Equals(metadata.description, value, StringComparison.Ordinal))
                    return ContentEditResult.NoChange("Description is already that.");

                string before = metadata.description ?? "";
                metadata.description = value;
                return ContentEditResult.Ok($"Description set on '{packageId}'.{note}", before, value);
            });

        /// <summary>Sets a package's authoring version.</summary>
        /// <param name="packageId">The package to change.</param>
        /// <param name="version">The new version. Non-semantic values are accepted and reported by validation.</param>
        /// <returns>The result.</returns>
        public ContentEditResult SetVersion(string packageId, string version) =>
            MutateMetadata(packageId, "Set Version", (metadata, note) =>
            {
                string value = version?.Trim() ?? "";
                if (string.Equals(metadata.version, value, StringComparison.Ordinal))
                    return ContentEditResult.NoChange("Version is already that.");

                string before = metadata.version ?? "";
                metadata.version = value;
                return ContentEditResult.Ok($"Version set on '{packageId}'.{note}", before, value);
            });

        /// <summary>Sets a package's author.</summary>
        /// <param name="packageId">The package to change.</param>
        /// <param name="author">The new author.</param>
        /// <returns>The result.</returns>
        public ContentEditResult SetAuthor(string packageId, string author) =>
            MutateMetadata(packageId, "Set Author", (metadata, note) =>
            {
                string value = author?.Trim() ?? "";
                if (string.Equals(metadata.author, value, StringComparison.Ordinal))
                    return ContentEditResult.NoChange("Author is already that.");

                string before = metadata.author ?? "";
                metadata.author = value;
                return ContentEditResult.Ok($"Author set on '{packageId}'.{note}", before, value);
            });

        /// <summary>
        /// Replaces a package's tags, dropping blank entries.
        /// </summary>
        /// <param name="packageId">The package to change.</param>
        /// <param name="tags">The tags to set.</param>
        /// <returns>The result.</returns>
        /// <remarks>
        /// Blanks are dropped rather than stored because nothing validates a tag, so an empty one would
        /// never be reported — and the list control that feeds this holds a half-typed row locally on
        /// exactly that promise.
        /// </remarks>
        public ContentEditResult SetTags(string packageId, IEnumerable<string> tags) =>
            MutateMetadata(packageId, "Set Tags", (metadata, note) =>
            {
                var next = (tags ?? Enumerable.Empty<string>())
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Select(tag => tag.Trim())
                    .ToArray();

                string before = string.Join(", ", metadata.tags ?? Array.Empty<string>());
                string after = string.Join(", ", next);
                if (string.Equals(before, after, StringComparison.Ordinal))
                    return ContentEditResult.NoChange("Tags are already that.");

                metadata.tags = next;
                return ContentEditResult.Ok($"Tags set on '{packageId}'.{note}", before, after);
            });

        // ── Flags ────────────────────────────────────────────────────────────

        /// <summary>Shows or hides a package in the content manager.</summary>
        /// <param name="packageId">The package to change.</param>
        /// <param name="visible">Whether players see it.</param>
        /// <returns>The result.</returns>
        /// <remarks>
        /// Hiding never affects correctness — a hidden package still installs, still resolves, and is
        /// still validated. That is deliberate: the surfaces that used to skip invisible packages are
        /// how a hidden required package that shipped nothing went unnoticed.
        /// </remarks>
        public ContentEditResult SetVisible(string packageId, bool visible) =>
            Mutate(packageId, "Set Package Visibility", config =>
            {
                if (config.isVisible == visible)
                    return ContentEditResult.NoChange($"Visibility is already {visible}.");

                config.isVisible = visible;
                return ContentEditResult.Ok(
                    $"'{packageId}' is now {(visible ? "visible" : "hidden")}.",
                    (!visible).ToString(), visible.ToString());
            });

        /// <summary>
        /// Marks a package required or optional.
        /// </summary>
        /// <param name="packageId">The package to change.</param>
        /// <param name="required">Whether it is auto-installed and non-uninstallable.</param>
        /// <returns>The result; the message names optional dependencies that now block publishing.</returns>
        /// <remarks>
        /// Making a package required does not make its dependencies required, and a required package
        /// that depends on an optional one is a blocking <c>required_depends_on_optional</c> finding —
        /// the optional one can be uninstalled out from under it. The dependencies are named here rather
        /// than promoted, because which of them should also become required is the author's call.
        /// </remarks>
        public ContentEditResult SetRequired(string packageId, bool required) =>
            Mutate(packageId, "Set Package Required", config =>
            {
                if (config.isRequired == required)
                    return ContentEditResult.NoChange($"Required is already {required}.");

                config.isRequired = required;

                string note = "";
                if (required)
                {
                    var optional = (config.dependencies ?? Array.Empty<ContentPackageSettings.PackageDependency>())
                        .Select(dependency => dependency?.packageId)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Select(Find)
                        .Where(dependency => dependency != null && !dependency.isRequired)
                        .Select(dependency => dependency.packageId)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    if (optional.Count > 0)
                        note = $" Depends on optional package(s) {string.Join(", ", optional)}, which blocks " +
                               "publishing until they are required too.";
                }

                return ContentEditResult.Ok(
                    $"'{packageId}' is now {(required ? "required" : "optional")}.{note}",
                    (!required).ToString(), required.ToString());
            });

        // ── Labels ───────────────────────────────────────────────────────────

        /// <summary>Adds one Addressables label to a package.</summary>
        /// <param name="packageId">The package to change.</param>
        /// <param name="label">The label to add.</param>
        /// <returns>The result.</returns>
        public ContentEditResult AddLabel(string packageId, string label) =>
            Mutate(packageId, "Add Label", config =>
            {
                string value = label?.Trim() ?? "";
                if (value.Length == 0)
                    return ContentEditResult.NoChange("An empty label matches nothing.");

                var labels = config.addressableLabels ?? Array.Empty<string>();
                if (labels.Contains(value, StringComparer.Ordinal))
                    return ContentEditResult.NoChange($"'{packageId}' already declares '{value}'.");

                string before = string.Join(", ", labels);
                config.addressableLabels = labels.Concat(new[] { value }).ToArray();
                return ContentEditResult.Ok(
                    $"Added label '{value}' to '{packageId}'.", before,
                    string.Join(", ", config.addressableLabels));
            });

        /// <summary>Removes one Addressables label from a package.</summary>
        /// <param name="packageId">The package to change.</param>
        /// <param name="label">The label to remove.</param>
        /// <returns>The result.</returns>
        public ContentEditResult RemoveLabel(string packageId, string label) =>
            Mutate(packageId, "Remove Label", config =>
            {
                var labels = config.addressableLabels ?? Array.Empty<string>();
                var kept = labels.Where(entry => !string.Equals(entry, label, StringComparison.Ordinal)).ToArray();

                if (kept.Length == labels.Length)
                    return ContentEditResult.NoChange($"'{packageId}' does not declare '{label}'.");

                string before = string.Join(", ", labels);
                config.addressableLabels = kept;
                return ContentEditResult.Ok(
                    $"Removed label '{label}' from '{packageId}'.", before, string.Join(", ", kept));
            });

        // ── Dependencies ─────────────────────────────────────────────────────

        /// <summary>
        /// Adds a dependency edge from one package to another.
        /// </summary>
        /// <param name="packageId">The depending package.</param>
        /// <param name="targetPackageId">The package it should depend on.</param>
        /// <returns>The result; the message names a cycle this edge would close.</returns>
        /// <remarks>
        /// A self-dependency is refused — it is never meaningful, and the mistake it usually represents
        /// is naming the wrong package. A <em>cycle</em> is not refused: it is a blocking
        /// <c>dependency_cycle</c> finding that publishing already stops, and an author mid-way through
        /// re-pointing several edges has a legitimate reason to pass through one. The message says the
        /// cycle exists so it cannot be closed by accident and left unnoticed.
        /// </remarks>
        public ContentEditResult AddDependency(string packageId, string targetPackageId) =>
            Mutate(packageId, "Add Dependency", config =>
            {
                string target = targetPackageId?.Trim() ?? "";
                if (target.Length == 0)
                    return ContentEditResult.NoChange("No dependency target given.");
                if (string.Equals(target, config.packageId, StringComparison.Ordinal))
                    return ContentEditResult.NoChange(
                        $"'{packageId}' cannot depend on itself. Another package was probably meant.");

                var dependencies = config.dependencies ?? Array.Empty<ContentPackageSettings.PackageDependency>();
                if (dependencies.Any(dependency =>
                        string.Equals(dependency?.packageId, target, StringComparison.Ordinal)))
                {
                    return ContentEditResult.NoChange($"'{packageId}' already depends on '{target}'.");
                }

                string before = string.Join(", ", dependencies.Select(dependency => dependency?.packageId));
                config.dependencies = dependencies
                    .Concat(new[] { new ContentPackageSettings.PackageDependency { packageId = target } })
                    .ToArray();

                var cycle = FindCycleThrough(config.packageId);
                string note = cycle != null
                    ? $" This closes a cycle: {string.Join(" -> ", cycle)}. Nothing in it can be installed."
                    : "";

                return ContentEditResult.Ok(
                    $"'{packageId}' now depends on '{target}'.{note}", before,
                    string.Join(", ", config.dependencies.Select(dependency => dependency.packageId)));
            });

        /// <summary>Removes every dependency edge from one package to another.</summary>
        /// <param name="packageId">The depending package.</param>
        /// <param name="targetPackageId">The dependency to drop.</param>
        /// <returns>The result.</returns>
        public ContentEditResult RemoveDependency(string packageId, string targetPackageId) =>
            Mutate(packageId, "Remove Dependency", config =>
            {
                var dependencies = config.dependencies ?? Array.Empty<ContentPackageSettings.PackageDependency>();
                var kept = dependencies
                    .Where(dependency => dependency != null &&
                                         !string.Equals(dependency.packageId, targetPackageId, StringComparison.Ordinal))
                    .ToArray();

                if (kept.Length == dependencies.Length)
                    return ContentEditResult.NoChange($"'{packageId}' does not depend on '{targetPackageId}'.");

                string before = string.Join(", ", dependencies.Select(dependency => dependency?.packageId));
                config.dependencies = kept;
                return ContentEditResult.Ok(
                    $"'{packageId}' no longer depends on '{targetPackageId}'.", before,
                    string.Join(", ", kept.Select(dependency => dependency.packageId)));
            });

        /// <summary>
        /// Replaces a package's dependency list.
        /// </summary>
        /// <param name="packageId">The depending package.</param>
        /// <param name="targetPackageIds">The ids to depend on. Blank entries are dropped.</param>
        /// <returns>The result.</returns>
        public ContentEditResult SetDependencies(string packageId, IEnumerable<string> targetPackageIds) =>
            Mutate(packageId, "Set Dependencies", config =>
            {
                var next = (targetPackageIds ?? Enumerable.Empty<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .ToArray();

                var dependencies = config.dependencies ?? Array.Empty<ContentPackageSettings.PackageDependency>();
                string before = string.Join(", ", dependencies.Select(dependency => dependency?.packageId));
                string after = string.Join(", ", next);
                if (string.Equals(before, after, StringComparison.Ordinal))
                    return ContentEditResult.NoChange("Dependencies are already that.");

                config.dependencies = next
                    .Select(id => new ContentPackageSettings.PackageDependency { packageId = id })
                    .ToArray();
                return ContentEditResult.Ok($"Dependencies set on '{packageId}'.", before, after);
            });

        // ── Delivery settings ────────────────────────────────────────────────

        /// <summary>Sets the legacy remote Addressables catalog URL.</summary>
        /// <param name="url">The catalog URL, or empty to disable.</param>
        /// <returns>The result.</returns>
        public ContentEditResult SetRemoteCatalogUrl(string url) =>
            SetSerializedField("_remoteCatalogUrl", property => property.stringValue,
                property => property.stringValue = url?.Trim() ?? "",
                value => value?.ToString() ?? "", "Set Remote Catalog Url");

        /// <summary>Sets the legacy <c>packages.json</c> manifest URL.</summary>
        /// <param name="url">The manifest URL, or empty to disable remote manifest fetching.</param>
        /// <returns>The result.</returns>
        public ContentEditResult SetRemotePackagesManifestUrl(string url) =>
            SetSerializedField("_remotePackagesManifestUrl", property => property.stringValue,
                property => property.stringValue = url?.Trim() ?? "",
                value => value?.ToString() ?? "", "Set Remote Manifest Url");

        /// <summary>Sets whether Addressables catalog updates are checked on startup.</summary>
        /// <param name="enabled">Whether to check.</param>
        /// <returns>The result.</returns>
        public ContentEditResult SetCheckForCatalogUpdates(bool enabled) =>
            SetSerializedField("_checkForCatalogUpdates", property => property.boolValue,
                property => property.boolValue = enabled,
                value => value.ToString(), "Set Catalog Update Check");

        /// <summary>Sets how many times a failed operation is retried.</summary>
        /// <param name="attempts">Attempts, clamped to 0..10.</param>
        /// <returns>The result.</returns>
        public ContentEditResult SetMaxRetryAttempts(int attempts) =>
            SetSerializedField("_maxRetryAttempts", property => property.intValue,
                property => property.intValue = Mathf.Clamp(attempts, 0, 10),
                value => value.ToString(), "Set Max Retry Attempts");

        /// <summary>Sets whether the package system logs verbosely.</summary>
        /// <param name="enabled">Whether to log verbosely.</param>
        /// <returns>The result.</returns>
        public ContentEditResult SetVerboseLogging(bool enabled) =>
            SetSerializedField("_enableVerboseLogging", property => property.boolValue,
                property => property.boolValue = enabled,
                value => value.ToString(), "Set Verbose Logging");

        /// <summary>
        /// Sets whether <c>packages.json</c> is read as a multi-version index.
        /// </summary>
        /// <param name="enabled">Whether content versioning is on.</param>
        /// <returns>The result.</returns>
        /// <remarks>
        /// This changes how the deployed manifest is parsed, not just what the app does with it: on, the
        /// file must be a <c>ContentVersionIndex</c>; off, a flat <c>RemotePackageManifest</c>. Flipping
        /// it against a manifest of the other shape fails at fetch time on a player's device.
        /// </remarks>
        public ContentEditResult SetContentVersioning(bool enabled) =>
            SetSerializedField("_enableContentVersioning", property => property.boolValue,
                property => property.boolValue = enabled,
                value => value.ToString(), "Set Content Versioning");

        /// <summary>Sets the app version used to filter compatible content versions.</summary>
        /// <param name="version">The version, or empty to use <c>Application.version</c>.</param>
        /// <returns>The result.</returns>
        public ContentEditResult SetAppVersion(string version) =>
            SetSerializedField("_appVersion", property => property.stringValue,
                property => property.stringValue = version?.Trim() ?? "",
                value => value?.ToString() ?? "", "Set App Version");

        /// <summary>Sets how many packages install concurrently.</summary>
        /// <param name="count">Concurrent downloads, clamped to 1..16.</param>
        /// <returns>The result.</returns>
        public ContentEditResult SetMaxConcurrentDownloads(int count) =>
            SetSerializedField("_maxConcurrentDownloads", property => property.intValue,
                property => property.intValue = Mathf.Clamp(count, 1, 16),
                value => value.ToString(), "Set Max Concurrent Downloads");

        /// <summary>Sets the soft cache cap in bytes.</summary>
        /// <param name="bytes">The cap; 0 means unlimited, negatives are clamped to 0.</param>
        /// <returns>The result.</returns>
        public ContentEditResult SetMaxCacheBytes(long bytes) =>
            SetSerializedField("_maxCacheBytes", property => property.longValue,
                property => property.longValue = Math.Max(0L, bytes),
                value => value.ToString(), "Set Max Cache Bytes");

        // ── Internals ────────────────────────────────────────────────────────

        private ContentPackageSettings.PackageConfig Find(string packageId) =>
            _settings.packageConfigs.FirstOrDefault(entry => entry?.packageId == packageId);

        /// <summary>
        /// Runs a change against a package's metadata, creating the metadata block when it is missing.
        /// </summary>
        /// <param name="packageId">The package to change.</param>
        /// <param name="undoLabel">Undo entry name.</param>
        /// <param name="change">
        /// The change. Receives the metadata and a note to append to a successful message, which is
        /// non-empty when the metadata block had to be created.
        /// </param>
        /// <remarks>
        /// A block created for a change that then turns out to be a no-op is put back. Otherwise setting
        /// a field to the value it already has would leave the config carrying a version it did not have
        /// before, dirtied by nothing and reported as no change.
        /// </remarks>
        private ContentEditResult MutateMetadata(
            string packageId,
            string undoLabel,
            Func<ContentPackageSettings.PackageMetadata, string, ContentEditResult> change)
        {
            return Mutate(packageId, undoLabel, config =>
            {
                string note = "";
                bool created = config.metadata == null;
                if (created)
                {
                    config.metadata = new ContentPackageSettings.PackageMetadata();
                    note = " Gave it a metadata block, so it now also has version " +
                           $"{config.metadata.version} where it had none.";
                }

                var result = change(config.metadata, note);
                if (created && !result.Changed) config.metadata = null;
                return result;
            });
        }

        /// <summary>
        /// Returns a dependency cycle passing through a package, or null.
        /// </summary>
        /// <param name="packageId">Where to start and end.</param>
        /// <remarks>
        /// A local walk rather than a call into <see cref="ContentValidation"/>: that engine reports
        /// every cycle in the whole definition set, and this only needs to know whether the edge just
        /// added closed one — a caller who wants the full picture runs the validator.
        /// </remarks>
        private List<string> FindCycleThrough(string packageId)
        {
            var byId = _settings.packageConfigs
                .Where(config => config != null && !string.IsNullOrWhiteSpace(config.packageId))
                .GroupBy(config => config.packageId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            var path = new List<string>();
            var onPath = new HashSet<string>(StringComparer.Ordinal);

            bool Walk(string id)
            {
                path.Add(id);

                // Back at the start: this is the cycle asked about, and `path` reads as one.
                if (path.Count > 1 && string.Equals(id, packageId, StringComparison.Ordinal))
                    return true;

                // A repeat of some other node is a cycle that does not pass through the start. It was
                // there before this edge, so reporting it here would blame the wrong change.
                if (!onPath.Add(id))
                {
                    path.RemoveAt(path.Count - 1);
                    return false;
                }

                if (byId.TryGetValue(id, out var config))
                {
                    foreach (var dependency in config.dependencies
                                               ?? Array.Empty<ContentPackageSettings.PackageDependency>())
                    {
                        string next = dependency?.packageId;
                        if (string.IsNullOrWhiteSpace(next) || !byId.ContainsKey(next)) continue;
                        if (Walk(next)) return true;
                    }
                }

                path.RemoveAt(path.Count - 1);
                onPath.Remove(id);
                return false;
            }

            return Walk(packageId) ? path : null;
        }
    }
}
