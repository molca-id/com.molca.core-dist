using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Molca.Editor.UI.Components;
using Molca.Localization;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub.Sections
{
    /// <summary>One stable string-table entry with all of its locale cells.</summary>
    internal sealed class LocalizationCatalogEntryView
    {
        internal LocalizationCatalogEntryView(IReadOnlyList<LocalizationCatalogCell> cells)
        {
            Cells = cells ?? System.Array.Empty<LocalizationCatalogCell>();
            var first = Cells.First();
            CollectionId = first.CollectionId;
            CollectionName = first.CollectionName;
            EntryId = first.EntryId;
            Key = first.Key;
        }

        internal string CollectionId { get; }
        internal string CollectionName { get; }
        internal long EntryId { get; }
        internal string Key { get; }
        internal IReadOnlyList<LocalizationCatalogCell> Cells { get; }
        internal int MissingCount => Cells.Count(cell => cell.IsMissing);
        internal int ReadOnlyCount => Cells.Count(cell => cell.IsReadOnly);

        internal LocalizationCatalogCell PreviewCell(string preferredLocale) =>
            Cells.FirstOrDefault(cell => string.Equals(
                cell.LocaleCode, preferredLocale, System.StringComparison.OrdinalIgnoreCase))
            ?? Cells.First();
    }

    /// <summary>
    /// Localization Hub foundation that renders the same audit snapshot used by Doctor, builds, and MCP.
    /// </summary>
    internal sealed class MolcaHubLocalizationSection : VisualElement
    {
        private const int MaxRenderedFindings = 100;
        private readonly VisualElement _content;

        /// <summary>Creates the section, optionally deferring its project-wide scans.</summary>
        /// <param name="deferInitialScan">
        /// When true, renders a lightweight landing card until the author explicitly starts the audit and
        /// authoring session. This keeps Hub restoration and domain reloads responsive in large projects.
        /// </param>
        internal MolcaHubLocalizationSection(bool deferInitialScan = true)
        {
            AddToClassList("molca-workspace");
            AddToClassList("molca-hub-localization-section");
            _content = new VisualElement();
            Add(_content);
            if (deferInitialScan)
                BuildLanding();
            else
                Rebuild(production: false);
        }

        private void BuildLanding()
        {
            _content.Clear();
            var card = new MolcaSectionCard(
                "Localization Workspace",
                "Audit, author, migrate, preview, and publish localized content.",
                MolcaStatusKind.Idle,
                "Ready",
                "Loading the complete surface scans project assets, catalogs, and loaded scenes. " +
                "It starts only when requested so restoring this workspace never blocks a domain reload.");
            card.Body.Add(WrappedLabel(
                "Run the shared interactive audit to open locale policy, catalog editing, CSV round trips, " +
                "pseudo-localization, migration, remote catalogs, and findings."));
            card.AddHeaderAction(new Button(() => Rebuild(production: false))
            {
                text = "Run Audit & Open",
            });
            _content.Add(card);
        }

        private void Rebuild(bool production)
        {
            _content.Clear();
            var request = production
                ? LocalizationAuditRequest.CreateBuildRequest(production: true)
                : LocalizationAuditRequest.CreateDoctorRequest();
            var snapshot = LocalizationAuditEngine.Audit(request);

            BuildOverview(snapshot, production);
            BuildLocales(snapshot);
            BuildPseudoPreview();
            BuildCatalog();
            BuildAuthoring();
            BuildArchiveAuthoring();
            BuildImportExport();
            BuildRemoteCatalog();
            BuildValueMigration();
            BuildFindings(snapshot);
        }

        private void BuildOverview(LocalizationAuditSnapshot snapshot, bool production)
        {
            var status = snapshot.Status == LocalizationAuditStatus.Failed ||
                         snapshot.Errors.Count > 0
                ? MolcaStatusKind.Error
                : snapshot.Warnings.Count > 0 || !snapshot.Coverage.IsComplete
                    ? MolcaStatusKind.Warning
                    : MolcaStatusKind.Ok;
            var card = new MolcaSectionCard(
                production ? "Production Preflight" : "Localization Audit",
                $"{snapshot.ConfiguredLocales.Count} locale(s) · {snapshot.Findings.Count} finding(s)",
                status,
                snapshot.Status.ToString(),
                "This is the shared snapshot consumed by Doctor, the player-build gate, MCP, and Hub.");

            var coverage = new Label($"Coverage: {snapshot.Coverage.Describe()}");
            coverage.AddToClassList("molca-hub-muted");
            card.Body.Add(coverage);

            var fingerprint = new Label($"Catalog fingerprint: {snapshot.CatalogFingerprint}");
            fingerprint.AddToClassList("molca-hub-muted");
            fingerprint.style.whiteSpace = WhiteSpace.Normal;
            card.Body.Add(fingerprint);

            card.AddHeaderAction(new Button(() => Rebuild(production: false)) { text = "Rescan" });
            card.AddHeaderAction(new Button(() => Rebuild(production: true))
            {
                text = "Production Preflight"
            });
            card.AddHeaderAction(new Button(() => MolcaHubWindow.OpenWorkspace("doctor"))
            {
                text = "Open Doctor"
            });
            _content.Add(card);
        }

        private void BuildLocales(LocalizationAuditSnapshot snapshot)
        {
            var card = new MolcaSectionCard(
                "Locales",
                snapshot.ConfiguredLocales.Count == 0
                    ? "No configured locale policy"
                    : string.Join(", ", snapshot.ConfiguredLocales));
            if (snapshot.ConfiguredLocales.Count == 0)
            {
                var empty = new Label(
                    "Create one LocalizationModule, register it in GlobalSettings, and add matching " +
                    "Unity Locale assets before authoring localized content.");
                empty.style.whiteSpace = WhiteSpace.Normal;
                card.Body.Add(empty);
            }
            else
            {
                var list = new VisualElement();
                list.AddToClassList("molca-list");
                foreach (var module in AssetDatabase.FindAssets("t:LocalizationModule")
                             .Select(AssetDatabase.GUIDToAssetPath)
                             .Select(AssetDatabase.LoadAssetAtPath<LocalizationModule>)
                             .Where(module => module != null))
                {
                    var languages = LanguagesOrEmpty(module);
                    var missingProfiles = languages.Count(language => language.PresentationProfile == null);
                    var group = new MolcaListGroup(
                        module.name,
                        $"{languages.Count} locale(s)",
                        missingProfiles > 0 ? MolcaStatusKind.Warning : MolcaStatusKind.Ok,
                        missingProfiles > 0 ? $"{missingProfiles} need profiles" : "Complete");

                    foreach (var language in languages)
                    {
                        var profile = language.PresentationProfile;
                        var subtitle = profile == null
                            ? "Presentation profile missing"
                            : profile.WritingDirection.ToString();
                        var row = new MolcaListRow(language.Code, subtitle);

                        var font = new Label(profile?.PrimaryFont == null
                            ? "Font missing"
                            : profile.PrimaryFont.name);
                        font.AddToClassList("molca-list-row__value");
                        row.AddMetadata(font);

                        var glyphs = profile?.GetMissingRequiredCharacters().Count ?? 0;
                        if (profile == null || glyphs > 0)
                            row.AddMetadata(new MolcaStatusBadge(
                                MolcaStatusKind.Warning,
                                profile == null ? "Profile missing" : $"{glyphs} glyphs missing"));

                        if (profile == null)
                        {
                            row.AddAction(MolcaButtons.Mini("Create Profile",
                                () => CreatePresentationProfile(module, language.Code)));
                        }
                        else
                        {
                            row.AddAction(MolcaButtons.Mini("Select Profile", () =>
                            {
                                Selection.activeObject = profile;
                                EditorGUIUtility.PingObject(profile);
                            }));
                        }

                        row.AddDetail(ListDetailRow(
                            "Fallback chain",
                            string.Join(" → ", module.GetFallbackChain(language.Code))));
                        row.AddDetail(ListDetailRow(
                            "Required glyphs",
                            profile == null ? "Unknown until a profile is assigned" : $"{glyphs} missing"));
                        group.Body.Add(row);
                    }

                    list.Add(group);
                }
                card.Body.Add(list);
            }
            _content.Add(card);
        }

        /// <summary>
        /// Returns a safe language view for legacy or partially initialized module assets.
        /// </summary>
        internal static System.Collections.Generic.IReadOnlyList<LocalizationModule.LanguageEntry>
            LanguagesOrEmpty(LocalizationModule module) =>
            module?.Languages ?? System.Array.Empty<LocalizationModule.LanguageEntry>();

        private void CreatePresentationProfile(
            LocalizationModule module,
            string localeCode)
        {
            var directionChoice = EditorUtility.DisplayDialogComplex(
                "Writing Direction",
                $"Choose the authored writing direction for '{localeCode}'.",
                "Left-to-right",
                "Cancel",
                "Right-to-left");
            if (directionChoice == 1)
                return;
            var direction = directionChoice == 2
                ? LocalizationWritingDirection.RightToLeft
                : LocalizationWritingDirection.LeftToRight;
            var defaultName = $"LocalePresentation-{localeCode.Replace('-', '_')}.asset";
            var path = EditorUtility.SaveFilePanelInProject(
                "Create Locale Presentation Profile",
                defaultName,
                "asset",
                "Choose a project-owned location for the locale presentation profile.");
            if (string.IsNullOrWhiteSpace(path))
                return;

            var profile = ScriptableObject.CreateInstance<LocalePresentationProfile>();
            try
            {
                var profileSerialized = new SerializedObject(profile);
                profileSerialized.FindProperty("writingDirection").enumValueIndex = (int)direction;
                profileSerialized.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.CreateAsset(profile, path);
                Undo.RegisterCreatedObjectUndo(profile, "Create Locale Presentation Profile");

                var index = System.Array.FindIndex(
                    module.Languages,
                    language => string.Equals(
                        LocalizationModule.CanonicalizeLocaleCode(language.Code),
                        LocalizationModule.CanonicalizeLocaleCode(localeCode),
                        System.StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                    throw new System.InvalidOperationException(
                        $"Locale '{localeCode}' no longer exists in the selected module.");
                Undo.RecordObject(module, "Assign Locale Presentation Profile");
                var languages = module.Languages.ToArray();
                var language = languages[index];
                language.PresentationProfile = profile;
                languages[index] = language;
                module.Languages = languages;
                EditorUtility.SetDirty(module);
                AssetDatabase.SaveAssets();
                Selection.activeObject = profile;
                EditorGUIUtility.PingObject(profile);
                Rebuild(production: false);
            }
            catch (System.Exception exception)
            {
                if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(profile)))
                    AssetDatabase.DeleteAsset(path);
                else
                    Object.DestroyImmediate(profile);
                EditorUtility.DisplayDialog(
                    "Profile Creation Failed",
                    exception.Message,
                    "OK");
            }
        }

        private void BuildPseudoPreview()
        {
            var card = new MolcaSectionCard(
                "Globalization Preview",
                "Stress-test expansion, missing-key visibility, and RTL without changing source text.");
            var profile = new EnumField(
                "Profile",
                LocalizationPseudoProfile.AccentExpansion);
            var source = new TextField("Source")
            {
                multiline = true,
                value = "Welcome, {playerName}! You have {count:plural:one item|{} items}.",
            };
            var output = new TextField("Preview")
            {
                multiline = true,
                isReadOnly = true,
            };
            var overflow = new Label();
            overflow.style.whiteSpace = WhiteSpace.Normal;

            void RefreshPreview() =>
                output.value = LocalizationPseudoPreviewService.Transform(
                    source.value,
                    (LocalizationPseudoProfile)profile.value);
            profile.RegisterValueChangedCallback(_ => RefreshPreview());
            source.RegisterValueChangedCallback(_ => RefreshPreview());

            card.Body.Add(profile);
            card.Body.Add(source);
            card.Body.Add(output);
            card.Body.Add(new Button(() =>
            {
                var rows = LocalizationPseudoPreviewService.ScanLoadedUi(
                    (LocalizationPseudoProfile)profile.value);
                overflow.text = rows.Count == 0
                    ? "No loaded LocalizedText overflow detected."
                    : $"{rows.Count} loaded LocalizedText overflow(s): " +
                      string.Join(", ", rows.Take(8).Select(row => row.Path));
            }) { text = "Scan Loaded UI for Overflow" });
            card.Body.Add(overflow);
            RefreshPreview();
            _content.Add(card);
        }

        private void BuildAuthoring()
        {
            var card = new MolcaSectionCard(
                "Add or Repair Locale",
                "Preview every affected asset before executing one verified transaction.");
            var code = new TextField("BCP-47 code");
            var displayName = new TextField("Display name (optional)");
            var modulePath = new TextField("Module path (required when ambiguous)");
            var preview = new VisualElement();
            card.Body.Add(code);
            card.Body.Add(displayName);
            card.Body.Add(modulePath);
            card.Body.Add(preview);

            card.AddHeaderAction(new Button(() =>
            {
                preview.Clear();
                var plan = LocalizationAuthoringService.PreviewAddLocale(
                    code.value,
                    displayName.value,
                    modulePath.value);
                var state = plan.IsExecutable
                    ? $"{plan.Changes.Count} change(s) ready"
                    : $"{plan.Errors.Count} blocking error(s)";
                preview.Add(WrappedLabel(
                    $"Plan {plan.PlanId}\nCatalog: {plan.SourceFingerprint}\n{state}"));

                foreach (var change in plan.Changes)
                    preview.Add(WrappedLabel($"• {change}"));
                foreach (var warning in plan.Warnings)
                    preview.Add(WrappedLabel($"Warning: {warning}"));
                foreach (var error in plan.Errors)
                    preview.Add(WrappedLabel($"Error: {error}"));

                if (!plan.IsExecutable)
                    return;
                preview.Add(new Button(() =>
                {
                    if (!EditorUtility.DisplayDialog(
                            "Execute Locale Transaction",
                            $"Apply {plan.Changes.Count} localization change(s) for '{plan.Code}'?\n\n" +
                            "Execution will be refused if the catalog changed after this preview.",
                            "Execute",
                            "Cancel"))
                        return;
                    var result = LocalizationAuthoringService.ExecuteAddLocale(plan);
                    if (!result.Succeeded)
                    {
                        EditorUtility.DisplayDialog(
                            result.WasStale ? "Preview Is Stale" : "Locale Transaction Failed",
                            result.Error,
                            "Close");
                        return;
                    }

                    EditorUtility.DisplayDialog(
                        "Locale Transaction Complete",
                        $"Configured '{plan.Code}' and verified the result with audit snapshot " +
                        $"{result.PostAudit.SnapshotId}.",
                        "Close");
                    Rebuild(production: false);
                })
                {
                    text = "Execute Transaction"
                });
            })
            {
                text = "Preview Changes"
            });
            _content.Add(card);
        }

        private void BuildCatalog()
        {
            const int maxRenderedEntries = 80;
            var snapshot = LocalizationCatalogAuthoringService.Capture();
            var entries = GroupCatalogEntries(snapshot.Cells);
            var collectionCount = entries
                .Select(entry => entry.CollectionId)
                .Distinct()
                .Count();
            var card = new MolcaSectionCard(
                "String Catalog",
                $"{collectionCount} collection(s) · {entries.Count} key(s) · " +
                $"{snapshot.Cells.Count(cell => cell.IsMissing)} missing cell(s)",
                snapshot.Cells.Any(cell => cell.IsReadOnly)
                    ? MolcaStatusKind.Warning
                    : MolcaStatusKind.Ok,
                null,
                "Each stable entry appears once; expand it to switch between locale values.");

            foreach (var warning in snapshot.Warnings)
                card.Body.Add(WrappedLabel($"Warning: {warning}"));

            var list = new VisualElement();
            list.AddToClassList("molca-list");
            var renderedEntries = entries.Take(maxRenderedEntries).ToList();
            var defaultLocale = LocalizationCatalogAuthoringService.GetDefaultLocaleCode();
            foreach (var collection in renderedEntries
                         .GroupBy(entry => new { entry.CollectionId, entry.CollectionName })
                         .OrderBy(group => group.Key.CollectionName, System.StringComparer.Ordinal))
            {
                var collectionEntries = collection.ToList();
                var missing = collectionEntries.Sum(entry => entry.MissingCount);
                var group = new MolcaListGroup(
                    collection.Key.CollectionName,
                    $"{collectionEntries.Count} key(s)",
                    missing > 0 ? MolcaStatusKind.Warning : MolcaStatusKind.Ok,
                    missing > 0 ? $"{missing} missing" : "Complete");

                foreach (var entry in collectionEntries)
                {
                    var previewCell = entry.PreviewCell(defaultLocale);
                    var row = new MolcaListRow(entry.Key, entry.CollectionName);
                    var renderedValue = new Label(
                        previewCell.IsMissing ? "— missing —" : previewCell.Value)
                    {
                        tooltip = $"{previewCell.LocaleCode} preview",
                    };
                    renderedValue.AddToClassList("molca-list-row__value");
                    row.AddMetadata(renderedValue);

                    var localeLabel = new Label(
                        $"{entry.Cells.Count} {(entry.Cells.Count == 1 ? "locale" : "locales")}");
                    localeLabel.AddToClassList("molca-list-row__meta");
                    row.AddMetadata(localeLabel);

                    if (entry.MissingCount > 0)
                        row.AddMetadata(new MolcaStatusBadge(
                            MolcaStatusKind.Warning,
                            $"{entry.MissingCount} missing"));
                    if (entry.ReadOnlyCount > 0)
                        row.AddMetadata(new MolcaStatusBadge(
                            MolcaStatusKind.Idle,
                            entry.ReadOnlyCount == entry.Cells.Count
                                ? "Read-only"
                                : $"{entry.ReadOnlyCount} read-only"));

                    row.AddDetail(BuildLocaleTabs(entry.Cells, defaultLocale));
                    row.AddDetail(ListDetailRow("Collection id", entry.CollectionId));
                    row.AddDetail(ListDetailRow("Entry id", entry.EntryId.ToString()));
                    group.Body.Add(row);
                }

                list.Add(group);
            }
            card.Body.Add(list);
            if (entries.Count > maxRenderedEntries)
                card.Body.Add(WrappedLabel(
                    $"{entries.Count - maxRenderedEntries} additional key(s). " +
                    "Use the collection id below or CSV export for bulk work."));

            var editor = new VisualElement();
            editor.Add(WrappedLabel(
                "Preview one cell edit. Set entry id to 0 with a new key to create an entry."));
            var collectionId = new TextField("Collection id");
            var entryId = new LongField("Entry id");
            var key = new TextField("Developer key");
            var locale = new TextField("Locale");
            var value = new TextField("Value") { multiline = true };
            var preview = new VisualElement();
            editor.Add(collectionId);
            editor.Add(entryId);
            editor.Add(key);
            editor.Add(locale);
            editor.Add(value);
            editor.Add(preview);
            editor.Add(new Button(() =>
            {
                preview.Clear();
                var plan = LocalizationCatalogAuthoringService.PreviewEdit(
                    collectionId.value,
                    entryId.value,
                    key.value,
                    locale.value,
                    value.value);
                preview.Add(WrappedLabel(
                    $"Plan {plan.PlanId}\nCatalog: {plan.SourceFingerprint}\n" +
                    $"{plan.Changes.Count} change(s), {plan.Errors.Count} error(s)"));
                foreach (var change in plan.Changes)
                    preview.Add(WrappedLabel($"• {change}"));
                foreach (var warning in plan.Warnings)
                    preview.Add(WrappedLabel($"Warning: {warning}"));
                foreach (var error in plan.Errors)
                    preview.Add(WrappedLabel($"Error: {error}"));
                if (!plan.IsExecutable)
                    return;
                preview.Add(new Button(() =>
                {
                    if (!EditorUtility.DisplayDialog(
                            "Edit Localization Catalog",
                            $"Apply the previewed value for '{plan.Key}' [{plan.LocaleCode}]?\n\n" +
                            "The edit is refused if the catalog changed after preview.",
                            "Apply",
                            "Cancel"))
                        return;
                    var result = LocalizationCatalogAuthoringService.ExecuteEdit(plan);
                    if (!result.Succeeded)
                    {
                        EditorUtility.DisplayDialog(
                            result.WasStale ? "Preview Is Stale" : "Catalog Edit Failed",
                            result.Error,
                            "Close");
                        return;
                    }
                    EditorUtility.DisplayDialog(
                        "Catalog Edit Complete",
                        $"Verified '{plan.Key}' [{plan.LocaleCode}] in audit snapshot " +
                        $"{result.PostAudit.SnapshotId}.",
                        "Close");
                    Rebuild(production: false);
                })
                {
                    text = "Apply Catalog Edit"
                });
            })
            {
                text = "Preview Catalog Edit"
            });
            card.Body.Add(editor);
            _content.Add(card);
        }

        /// <summary>Projects the catalog matrix into one stable row per collection entry.</summary>
        internal static IReadOnlyList<LocalizationCatalogEntryView> GroupCatalogEntries(
            IEnumerable<LocalizationCatalogCell> cells) =>
            (cells ?? System.Array.Empty<LocalizationCatalogCell>())
            .GroupBy(cell => new { cell.CollectionId, cell.EntryId })
            .Select(group => new LocalizationCatalogEntryView(group
                .OrderBy(cell => cell.LocaleCode, System.StringComparer.Ordinal)
                .ToList()))
            .OrderBy(entry => entry.CollectionName, System.StringComparer.Ordinal)
            .ThenBy(entry => entry.Key, System.StringComparer.Ordinal)
            .ThenBy(entry => entry.EntryId)
            .ToList();

        private static VisualElement BuildLocaleTabs(
            IReadOnlyList<LocalizationCatalogCell> cells,
            string preferredLocale)
        {
            var root = new VisualElement();

            var tabs = new VisualElement();
            tabs.AddToClassList("molca-workspace-subtabs");
            tabs.style.flexWrap = Wrap.Wrap;
            root.Add(tabs);

            var panel = new VisualElement();
            root.Add(panel);

            var buttons = new List<(Button Button, LocalizationCatalogCell Cell)>();
            void Select(LocalizationCatalogCell selected)
            {
                foreach (var tab in buttons)
                    tab.Button.EnableInClassList(
                        "molca-workspace-subtab--active",
                        object.ReferenceEquals(tab.Cell, selected));

                panel.Clear();
                panel.Add(ListDetailRow(
                    "Value",
                    selected.IsMissing ? "— missing —" : selected.Value));
                panel.Add(ListDetailRow("Locale", selected.LocaleCode));
                panel.Add(ListDetailRow("Format", selected.IsSmart ? "Smart string" : "Plain string"));
                panel.Add(ListDetailRow(
                    "Ownership",
                    selected.IsReadOnly ? "SDK-owned · read-only" : "Project-owned · editable"));
                panel.Add(ListDetailRow("Table asset", selected.TableAssetPath));
            }

            foreach (var cell in cells)
            {
                var button = new Button(() => Select(cell))
                {
                    text = cell.IsMissing ? $"{cell.LocaleCode} · Missing" : cell.LocaleCode,
                    tooltip = cell.IsMissing
                        ? $"{cell.LocaleCode}: missing value"
                        : cell.IsReadOnly
                            ? $"{cell.LocaleCode}: read-only"
                            : $"Show {cell.LocaleCode}",
                };
                button.AddToClassList("molca-workspace-subtab");
                tabs.Add(button);
                buttons.Add((button, cell));
            }

            var initial = cells.FirstOrDefault(cell => string.Equals(
                              cell.LocaleCode, preferredLocale, System.StringComparison.OrdinalIgnoreCase))
                          ?? cells.First();
            Select(initial);
            return root;
        }

        private static Label WrappedLabel(string text)
        {
            var label = new Label(text);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.AddToClassList("molca-hub-muted");
            return label;
        }

        private static VisualElement ListDetailRow(string label, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-list-detail-row");

            var key = new Label(label ?? string.Empty);
            key.AddToClassList("molca-list-detail-row__label");
            row.Add(key);

            var renderedValue = new Label(value ?? string.Empty);
            renderedValue.AddToClassList("molca-list-detail-row__value");
            row.Add(renderedValue);
            return row;
        }

        private void BuildArchiveAuthoring()
        {
            var card = new MolcaSectionCard(
                "Archive Locale",
                "Disable a locale everywhere while preserving authored assets and inline values.");
            var code = new TextField("Configured BCP-47 code");
            var modulePath = new TextField("Module path (required when ambiguous)");
            var preview = new VisualElement();
            card.Body.Add(code);
            card.Body.Add(modulePath);
            card.Body.Add(preview);

            card.AddHeaderAction(new Button(() =>
            {
                preview.Clear();
                var plan = LocalizationAuthoringService.PreviewArchiveLocale(
                    code.value,
                    modulePath.value);
                var state = plan.IsExecutable
                    ? $"{plan.Changes.Count} change(s) ready"
                    : $"{plan.Errors.Count} blocking error(s)";
                preview.Add(WrappedLabel(
                    $"Plan {plan.PlanId}\nCatalog: {plan.SourceFingerprint}\n{state}"));
                foreach (var change in plan.Changes)
                    preview.Add(WrappedLabel($"• {change}"));
                foreach (var warning in plan.Warnings)
                    preview.Add(WrappedLabel($"Preserved: {warning}"));
                foreach (var error in plan.Errors)
                    preview.Add(WrappedLabel($"Error: {error}"));

                if (!plan.IsExecutable)
                    return;
                preview.Add(new Button(() =>
                {
                    if (!EditorUtility.DisplayDialog(
                            "Archive Locale",
                            $"Disable '{plan.Code}' with {plan.Changes.Count} change(s)?\n\n" +
                            "Locale, table, and inline assets will be preserved. " +
                            "Execution is refused if the catalog changed after preview.",
                            "Archive",
                            "Cancel"))
                        return;
                    var result = LocalizationAuthoringService.ExecuteArchiveLocale(plan);
                    if (!result.Succeeded)
                    {
                        EditorUtility.DisplayDialog(
                            result.WasStale ? "Preview Is Stale" : "Locale Archive Failed",
                            result.Error,
                            "Close");
                        return;
                    }

                    EditorUtility.DisplayDialog(
                        "Locale Archived",
                        $"Disabled '{plan.Code}' without deleting authored assets. Audit snapshot: " +
                        $"{result.PostAudit.SnapshotId}.",
                        "Close");
                    Rebuild(production: false);
                })
                {
                    text = "Archive Locale"
                });
            })
            {
                text = "Preview Archive"
            });
            _content.Add(card);
        }

        private void BuildImportExport()
        {
            var card = new MolcaSectionCard(
                "Catalog Import / Export",
                "Stable-identity CSV round trips with all-or-nothing preview and placeholder checks.");
            var collectionId = new TextField("Collection id (optional)");
            var preview = new VisualElement();
            card.Body.Add(collectionId);
            card.Body.Add(preview);

            card.AddHeaderAction(new Button(() =>
            {
                var path = EditorUtility.SaveFilePanel(
                    "Export Molca Localization Catalog",
                    string.Empty,
                    "molca-localization.csv",
                    "csv");
                if (string.IsNullOrEmpty(path))
                    return;
                File.WriteAllText(
                    path,
                    LocalizationCatalogAuthoringService.ExportCsv(collectionId.value),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                EditorUtility.RevealInFinder(path);
            })
            {
                text = "Export CSV"
            });

            card.AddHeaderAction(new Button(() =>
            {
                var path = EditorUtility.OpenFilePanel(
                    "Preview Molca Localization Import",
                    string.Empty,
                    "csv");
                if (string.IsNullOrEmpty(path))
                    return;
                preview.Clear();
                var plan = LocalizationCatalogAuthoringService.PreviewCsvImport(
                    File.ReadAllText(path, Encoding.UTF8));
                preview.Add(WrappedLabel(
                    $"{Path.GetFileName(path)}\nPlan {plan.PlanId}\n" +
                    $"{plan.Changes.Count} change(s), {plan.Errors.Count} error(s)"));
                foreach (var change in plan.Changes.Take(100))
                    preview.Add(WrappedLabel(
                        $"• {change.Key} [{change.LocaleCode}]\n" +
                        $"{change.PreviousValue} → {change.Value}"));
                if (plan.Changes.Count > 100)
                    preview.Add(WrappedLabel(
                        $"{plan.Changes.Count - 100} additional change(s)."));
                foreach (var warning in plan.Warnings)
                    preview.Add(WrappedLabel($"Warning: {warning}"));
                foreach (var error in plan.Errors.Take(100))
                    preview.Add(WrappedLabel($"Error: {error}"));
                if (!plan.IsExecutable)
                    return;
                preview.Add(new Button(() =>
                {
                    if (!EditorUtility.DisplayDialog(
                            "Import Localization Catalog",
                            $"Apply all {plan.Changes.Count} previewed value change(s)?\n\n" +
                            "No rows apply if verification fails. The import is one Undo group.",
                            "Import All",
                            "Cancel"))
                        return;
                    var result = LocalizationCatalogAuthoringService.ExecuteCsvImport(plan);
                    if (!result.Succeeded)
                    {
                        EditorUtility.DisplayDialog(
                            result.WasStale ? "Preview Is Stale" : "Catalog Import Failed",
                            result.Error,
                            "Close");
                        return;
                    }
                    EditorUtility.DisplayDialog(
                        "Catalog Import Complete",
                        $"Applied and verified {plan.Changes.Count} value change(s).",
                        "Close");
                    Rebuild(production: false);
                })
                {
                    text = "Import All Changes"
                });
            })
            {
                text = "Preview CSV Import"
            });
            _content.Add(card);
        }

        private void BuildRemoteCatalog()
        {
            var module = AssetDatabase.FindAssets("t:LocalizationModule")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<LocalizationModule>)
                .FirstOrDefault(candidate => candidate != null);
            var settings = module?.RemoteCatalog;
            var status = settings == null
                ? MolcaStatusKind.Warning
                : !settings.Enabled
                    ? MolcaStatusKind.Idle
                    : settings.TrustedKeys.Count == 0 || settings.AllowedEntries.Count == 0
                        ? MolcaStatusKind.Warning
                        : MolcaStatusKind.Ok;
            var card = new MolcaSectionCard(
                "Remote Catalog",
                settings == null
                    ? "No remote catalog settings are assigned."
                    : $"{settings.Channel} · {settings.AllowedEntries.Count} shipped identities",
                status,
                settings?.Enabled == true ? "Enabled" : "Optional",
                "Exports immutable publication bundles. The server signs, audits, and activates versions; player builds contain public keys only.");

            if (module == null)
            {
                card.Body.Add(WrappedLabel(
                    "Create the authoritative LocalizationModule before configuring remote updates."));
                _content.Add(card);
                return;
            }
            if (settings == null)
            {
                card.Body.Add(WrappedLabel(
                    "Create and assign settings, then add the server public verification key and project id."));
                card.AddHeaderAction(new Button(() =>
                {
                    const string directory = "Assets/_Molca/Localization/Remote";
                    EnsureAssetFolders(directory);
                    const string path =
                        directory + "/LocalizationRemoteCatalogSettings.asset";
                    var created = AssetDatabase.LoadAssetAtPath<LocalizationRemoteCatalogSettings>(path);
                    if (created == null)
                    {
                        created = ScriptableObject.CreateInstance<LocalizationRemoteCatalogSettings>();
                        AssetDatabase.CreateAsset(created, path);
                    }
                    Undo.RecordObject(module, "Assign Localization Remote Catalog");
                    var serialized = new SerializedObject(module);
                    serialized.FindProperty("remoteCatalog").objectReferenceValue = created;
                    serialized.ApplyModifiedProperties();
                    EditorUtility.SetDirty(module);
                    AssetDatabase.SaveAssets();
                    Selection.activeObject = created;
                    Rebuild(production: false);
                })
                {
                    text = "Create Settings"
                });
                _content.Add(card);
                return;
            }

            var details = new Label(
                $"Project: {(string.IsNullOrWhiteSpace(settings.ProjectId) ? "not set" : settings.ProjectId)}\n" +
                $"Manifest: {(string.IsNullOrWhiteSpace(settings.ManifestUrl) ? "licensed server default" : settings.ManifestUrl)}\n" +
                $"Trusted keys: {settings.TrustedKeys.Count}\n" +
                $"Runtime: {(EditorApplication.isPlaying ? LocalizationManager.OverlayStatus.ToString() : "not playing")}");
            details.style.whiteSpace = WhiteSpace.Normal;
            details.AddToClassList("molca-hub-muted");
            card.Body.Add(details);

            var version = new TextField("Version") { value = Application.version };
            var baseVersion = new TextField("Base catalog version");
            var minApp = new TextField("Minimum app version");
            var maxApp = new TextField("Maximum app version");
            card.Body.Add(version);
            card.Body.Add(baseVersion);
            card.Body.Add(minApp);
            card.Body.Add(maxApp);

            card.AddHeaderAction(new Button(() =>
            {
                var count = LocalizationRemoteCatalogAuthoringService.SyncAllowlist(settings);
                EditorUtility.DisplayDialog(
                    "Remote Allowlist Updated",
                    $"Stored {count} stable catalog identities and placeholder contracts.",
                    "Close");
                Rebuild(production: false);
            })
            {
                text = "Sync Allowlist"
            });
            card.AddHeaderAction(new Button(() =>
            {
                var export = LocalizationRemoteCatalogAuthoringService.BuildBundle(
                    settings,
                    version.value,
                    baseVersion.value,
                    minApp.value,
                    maxApp.value);
                if (!export.Succeeded)
                {
                    EditorUtility.DisplayDialog(
                        "Remote Bundle Invalid",
                        export.Error,
                        "Close");
                    return;
                }
                var path = EditorUtility.SaveFilePanel(
                    "Export Remote Catalog Publication Bundle",
                    string.Empty,
                    $"molca-localization-{version.value}.json",
                    "json");
                if (string.IsNullOrEmpty(path))
                    return;
                File.WriteAllText(path, export.Json, new UTF8Encoding(false));
                EditorUtility.RevealInFinder(path);
                EditorUtility.DisplayDialog(
                    "Publication Bundle Exported",
                    $"{export.EntryCount} values · {export.LocaleCount} locales · " +
                    $"{export.SizeBytes} bytes\n\nPublish this bundle from the project dashboard. " +
                    "The server will normalize, sign, store, and audit it.",
                    "Close");
            })
            {
                text = "Export Publish Bundle"
            });
            card.AddHeaderAction(new Button(() =>
            {
                var export = LocalizationRemoteCatalogAuthoringService.BuildBundle(
                    settings,
                    version.value,
                    baseVersion.value,
                    minApp.value,
                    maxApp.value);
                if (!export.Succeeded)
                {
                    EditorUtility.DisplayDialog(
                        "Remote Bundle Invalid",
                        export.Error,
                        "Close");
                    return;
                }
                PublishRemoteCatalog(export);
            })
            {
                text = "Preview & Publish"
            });
            card.AddHeaderAction(new Button(() =>
            {
                Selection.activeObject = settings;
                EditorGUIUtility.PingObject(settings);
            })
            {
                text = "Edit Settings"
            });
            if (EditorApplication.isPlaying)
            {
                card.Body.Add(new Button(RefreshRemoteCatalog) { text = "Refresh Runtime Overlay" });
                card.Body.Add(new Button(() =>
                {
                    var result = LocalizationManager.RollbackRemoteCatalog();
                    EditorUtility.DisplayDialog(
                        result.Success ? "Overlay Rolled Back" : "Rollback Unavailable",
                        result.Message,
                        "Close");
                    Rebuild(production: false);
                })
                {
                    text = "Rollback Runtime Overlay"
                });
            }
            _content.Add(card);
        }

        private async void RefreshRemoteCatalog() // doctor:ignore — Unity UI callback owns completion.
        {
            var result = await LocalizationManager.RefreshRemoteCatalogAsync();
            EditorUtility.DisplayDialog(
                result.Success ? "Remote Catalog Refreshed" : "Remote Catalog Rejected",
                result.Success
                    ? $"{result.Message}\nVersion: {result.Version}"
                    : $"{result.DiagnosticCode}\n{result.Message}",
                "Close");
            Rebuild(production: false);
        }

        private async void PublishRemoteCatalog( // doctor:ignore — Unity UI callback owns completion.
            LocalizationRemoteCatalogExport export)
        {
            var client = new LocalizationRemoteCatalogEditorClient();
            var preview = await client.PreviewAsync(export.Json);
            if (!preview.Succeeded)
            {
                EditorUtility.DisplayDialog(
                    "Remote Publication Preview Failed",
                    preview.Error,
                    "Close");
                return;
            }
            if (!EditorUtility.DisplayDialog(
                    "Publish Immutable Remote Catalog",
                    $"Publish {preview.Channel}/{preview.Version}?\n\n" +
                    $"{preview.EntryCount} values · {preview.LocaleCount} locales · " +
                    $"{preview.SizeBytes} bytes\nSHA-256: {preview.Sha256}\n\n" +
                    "This version cannot be overwritten. Publishing atomically makes it active.",
                    "Publish",
                    "Cancel"))
                return;
            var published = await client.PublishAsync(export.Json);
            EditorUtility.DisplayDialog(
                published.Succeeded ? "Remote Catalog Published" : "Remote Publication Failed",
                published.Succeeded
                    ? $"{published.Channel}/{published.Version} is active.\nSHA-256: {published.Sha256}"
                    : published.Error,
                "Close");
            Rebuild(production: false);
        }

        private static void EnsureAssetFolders(string path)
        {
            var current = "Assets";
            foreach (var segment in path.Split('/').Skip(1))
            {
                var child = current + "/" + segment;
                if (!AssetDatabase.IsValidFolder(child))
                    AssetDatabase.CreateFolder(current, segment);
                current = child;
            }
        }

        private void BuildFindings(LocalizationAuditSnapshot snapshot)
        {
            var card = new MolcaSectionCard(
                "Findings",
                snapshot.Findings.Count == 0
                    ? "No findings in this scope"
                    : $"{snapshot.Errors.Count} error(s) · {snapshot.Warnings.Count} warning(s)");

            foreach (var finding in snapshot.Findings.Take(MaxRenderedFindings))
            {
                var location = string.IsNullOrEmpty(finding.Path)
                    ? string.Empty
                    : $"\n{finding.Path}" +
                      (string.IsNullOrEmpty(finding.PropertyPath)
                          ? string.Empty
                          : $" · {finding.PropertyPath}");
                var row = new Label(
                    $"[{finding.Severity}] {finding.Id}\n{finding.Message}{location}");
                row.style.whiteSpace = WhiteSpace.Normal;
                row.AddToClassList("molca-hub-muted");
                card.Body.Add(row);
            }

            if (snapshot.Findings.Count > MaxRenderedFindings)
                card.Body.Add(new Label(
                    $"{snapshot.Findings.Count - MaxRenderedFindings} additional finding(s). " +
                    "Open Doctor for filtering and navigation."));
            _content.Add(card);
        }

        private void BuildValueMigration()
        {
            var card = new MolcaSectionCard(
                "Localized Value Migration",
                "Scan legacy values on demand",
                MolcaStatusKind.Idle,
                "Not scanned",
                "Migrates retained DynamicLocalization payloads to explicit Catalog or Inline sources. " +
                "The project-wide inventory is explicit so opening the workspace stays responsive.");
            var preview = new VisualElement();
            card.Body.Add(WrappedLabel(
                "Scan and preview before applying. The preview is fingerprint-bound, and execution refuses " +
                "any project state that changed after the scan."));
            card.Body.Add(preview);
            card.AddHeaderAction(new Button(() =>
            {
                preview.Clear();
                var plan = LocalizationValueMigrationService.Preview();
                preview.Add(WrappedLabel(
                    $"Plan {plan.PlanId}\n{plan.Candidates.Count} legacy value(s) · " +
                    $"{plan.Changes.Count} change(s), " +
                    $"{plan.Warnings.Count} warning(s), {plan.Errors.Count} error(s)"));
                foreach (var change in plan.Changes.Take(100))
                    preview.Add(WrappedLabel($"• {change}"));
                foreach (var warning in plan.Warnings.Take(100))
                    preview.Add(WrappedLabel($"Warning: {warning}"));
                foreach (var error in plan.Errors)
                    preview.Add(WrappedLabel($"Error: {error}"));
                if (!plan.IsExecutable)
                    return;
                preview.Add(new Button(() =>
                {
                    if (!EditorUtility.DisplayDialog(
                            "Migrate Localized Values",
                            $"Migrate {plan.Changes.Count} previewed value(s) to schema v2?\n\n" +
                            "The plan will be refused if any value changed. The operation is one Undo group.",
                            "Migrate",
                            "Cancel"))
                        return;
                    var result = LocalizationValueMigrationService.Execute(plan);
                    if (!result.Succeeded)
                    {
                        EditorUtility.DisplayDialog(
                            result.WasStale ? "Preview Is Stale" : "Migration Failed",
                            result.Error,
                            "Close");
                        return;
                    }
                    EditorUtility.DisplayDialog(
                        "Migration Complete",
                        $"Migrated and verified {result.ChangedCount} value(s). " +
                        $"Audit snapshot: {result.PostAudit.SnapshotId}.",
                        "Close");
                    Rebuild(production: false);
                })
                {
                    text = "Execute Migration"
                });
            })
            {
                text = "Scan & Preview Migration"
            });
            _content.Add(card);
        }
    }
}
