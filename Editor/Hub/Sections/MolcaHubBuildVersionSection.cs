using System;
using Molca.Editor.UI.Components;
using Molca.Settings;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub.Sections
{
    /// <summary>
    /// Build &amp; Version section for the Molca Hub Settings workspace.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/Sections/</c>.
    /// Base class: <see cref="VisualElement"/>.
    /// Registration: created by <see cref="MolcaHubWindow"/> when the Build &amp; Version rail section is active.
    /// Ports the existing <see cref="BuildSettingsEditor"/> / <see cref="VersionSettingsEditor"/> UI into the
    /// design-handoff master-detail layout. All edits flow through <see cref="SerializedObject"/> /
    /// <see cref="SerializedProperty"/>; build execution stays in <see cref="BuildManager"/>. This view draws no
    /// build/version logic of its own — it only binds and dispatches.
    /// </remarks>
    internal sealed class MolcaHubBuildVersionSection : VisualElement
    {
        private const string BuildView = "Build";
        private const string VersionView = "Version";

        // The profile last applied to PlayerSettings from this Hub. ACTIVE is shown only for this
        // profile, and only while its config still matches the live PlayerSettings (see ProfileIsActive).
        private const string AppliedProfileKey = "Molca.Hub.AppliedBuildProfile";

        private readonly MolcaHubState _state;
        private readonly BuildSettings _buildSettings;
        private readonly VersionSettings _versionSettings;
        private readonly SerializedObject _buildSerialized;
        private readonly SerializedObject _versionSerialized;
        private readonly SerializedProperty _profiles;

        private Button _buildSegment;
        private Button _versionSegment;
        private VisualElement _viewContainer;
        private VisualElement _profileRail;
        private VisualElement _profileDetail;

        private int _selectedProfileIndex;

        internal MolcaHubBuildVersionSection(MolcaHubState state)
        {
            _state = state;
            AddToClassList("molca-hub-buildversion-section");

            _buildSettings = MolcaEditorSettings.Instance != null ? MolcaEditorSettings.Instance.BuildSettings : null;
            _versionSettings = MolcaEditorSettings.Instance != null ? MolcaEditorSettings.Instance.VersionSettings : null;

            if (_buildSettings == null || _versionSettings == null)
            {
                BuildMissingAssetNotice();
                return;
            }

            _buildSerialized = new SerializedObject(_buildSettings);
            _versionSerialized = new SerializedObject(_versionSettings);
            _profiles = _buildSerialized.FindProperty("profiles");
            _selectedProfileIndex = ResolveSelectedProfileIndex(_state.SelectedBuildProfile);

            BuildContextHeader();
            BuildSegmentedToggle();

            _viewContainer = new VisualElement();
            _viewContainer.AddToClassList("molca-hub-bv-view");
            Add(_viewContainer);

            BuildFooter();

            SelectView(_state.BuildVersionView);
        }

        private void BuildMissingAssetNotice()
        {
            var card = new MolcaSectionCard(
                "Build & Version",
                "Settings assets not assigned",
                MolcaStatusKind.Warning,
                "Misconfigured");

            var message = new Label(
                "Assign Build Settings and Version Settings on the Molca Editor Settings asset to manage build profiles and versioning here.");
            message.AddToClassList("molca-hub-muted");
            card.Body.Add(message);
            Add(card);
        }

        // -------------------------------------------------------------------
        // Context header + segmented toggle
        // -------------------------------------------------------------------

        private void BuildContextHeader()
        {
            var header = new VisualElement();
            header.AddToClassList("molca-hub-bv-context");
            Add(header);

            var marker = new VisualElement();
            marker.AddToClassList("molca-hub-bv-context__marker");
            header.Add(marker);

            var assetName = new Label(_buildSettings.name);
            assetName.AddToClassList("molca-hub-bv-context__asset");
            header.Add(assetName);

            var sep = new VisualElement();
            sep.AddToClassList("molca-hub-bv-context__sep");
            header.Add(sep);

            var target = new Label($"active target  {EditorUserBuildSettings.activeBuildTarget}");
            target.AddToClassList("molca-hub-bv-context__meta");
            header.Add(target);

            var version = new Label($"· v{_versionSettings.GetFullVersionString()}");
            version.AddToClassList("molca-hub-bv-context__meta");
            header.Add(version);

            var spacer = new VisualElement();
            spacer.AddToClassList("molca-hub-spacer");
            header.Add(spacer);

            var buildAll = new Button(BuildAllForActiveTarget)
            {
                text = "Build All",
                tooltip = $"Build every profile targeting {EditorUserBuildSettings.activeBuildTarget}."
            };
            buildAll.AddToClassList("molca-hub-bv-primary-pill");
            header.Add(buildAll);
        }

        private void BuildSegmentedToggle()
        {
            var segmented = new VisualElement();
            segmented.AddToClassList("molca-hub-bv-segmented");
            Add(segmented);

            _versionSegment = new Button(() => SelectView(VersionView)) { text = "Version" };
            _versionSegment.AddToClassList("molca-hub-bv-segment");
            segmented.Add(_versionSegment);

            _buildSegment = new Button(() => SelectView(BuildView)) { text = "Build" };
            _buildSegment.AddToClassList("molca-hub-bv-segment");
            segmented.Add(_buildSegment);
        }

        private void SelectView(string view)
        {
            var resolved = string.Equals(view, VersionView, StringComparison.OrdinalIgnoreCase) ? VersionView : BuildView;
            _state.SetBuildVersionView(resolved);

            _buildSegment.EnableInClassList("molca-hub-bv-segment--active", resolved == BuildView);
            _versionSegment.EnableInClassList("molca-hub-bv-segment--active", resolved == VersionView);

            _viewContainer.Clear();
            if (resolved == BuildView)
                BuildBuildView();
            else
                BuildVersionView();
        }

        // -------------------------------------------------------------------
        // Build view — profiles master/detail
        // -------------------------------------------------------------------

        private void BuildBuildView()
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-bv-build-row");
            _viewContainer.Add(row);

            _profileRail = new VisualElement();
            _profileRail.AddToClassList("molca-hub-bv-profile-rail");
            row.Add(_profileRail);

            _profileDetail = new VisualElement();
            _profileDetail.AddToClassList("molca-hub-bv-profile-detail");
            row.Add(_profileDetail);

            RebuildProfileRail();
            RebuildProfileDetail();
        }

        private void RebuildProfileRail()
        {
            _profileRail.Clear();

            var header = new VisualElement();
            header.AddToClassList("molca-hub-bv-rail-header");
            _profileRail.Add(header);

            var title = new Label("Profiles");
            title.AddToClassList("molca-hub-bv-rail-title");
            header.Add(title);

            var actions = new VisualElement();
            actions.AddToClassList("molca-hub-bv-rail-actions");
            header.Add(actions);

            var add = new Button(AddProfile) { text = "+", tooltip = "Add a build profile." };
            add.AddToClassList("molca-hub-bv-rail-button");
            actions.Add(add);

            var remove = new Button(RemoveSelectedProfile) { text = "−", tooltip = "Remove the selected build profile." };
            remove.AddToClassList("molca-hub-bv-rail-button");
            remove.SetEnabled(_profiles.arraySize > 0);
            actions.Add(remove);

            var profiles = _buildSettings.Profiles;
            for (int i = 0; i < profiles.Count; i++)
            {
                var profile = profiles[i];
                // Read the live profile object: BuildTarget is non-contiguous, so a SerializedProperty's
                // enumValueIndex is a popup index, not the BuildTarget value.
                _profileRail.Add(BuildProfileRow(i, profile.name, profile.target, ProfileIsActive(profile)));
            }
        }

        private VisualElement BuildProfileRow(int index, string name, BuildTarget target, bool isActiveTarget)
        {
            var row = new Button(() => SelectProfile(index));
            row.AddToClassList("molca-hub-bv-profile-row");
            row.EnableInClassList("molca-hub-bv-profile-row--selected", index == _selectedProfileIndex);

            var stack = new VisualElement();
            stack.AddToClassList("molca-hub-bv-profile-row__stack");
            row.Add(stack);

            var nameLabel = new Label(string.IsNullOrEmpty(name) ? "(unnamed)" : name);
            nameLabel.AddToClassList("molca-hub-bv-profile-row__name");
            stack.Add(nameLabel);

            var targetLabel = new Label(ShortTarget(target));
            targetLabel.AddToClassList("molca-hub-bv-profile-row__target");
            stack.Add(targetLabel);

            if (isActiveTarget)
            {
                var badge = new Label("ACTIVE");
                badge.AddToClassList("molca-hub-bv-active-badge");
                row.Add(badge);
            }

            return row;
        }

        private void SelectProfile(int index)
        {
            _selectedProfileIndex = index;
            if (index >= 0 && index < _profiles.arraySize)
                _state.SetSelectedBuildProfile(_profiles.GetArrayElementAtIndex(index).FindPropertyRelative("name").stringValue);

            RebuildProfileRail();
            RebuildProfileDetail();
        }

        private void RebuildProfileDetail()
        {
            _profileDetail.Clear();

            if (_selectedProfileIndex < 0 || _selectedProfileIndex >= _profiles.arraySize)
            {
                var empty = new Label("Select a profile to edit its settings.");
                empty.AddToClassList("molca-hub-muted");
                _profileDetail.Add(empty);
                return;
            }

            var profile = _profiles.GetArrayElementAtIndex(_selectedProfileIndex);

            var detailHeader = new VisualElement();
            detailHeader.AddToClassList("molca-hub-bv-detail-header");
            _profileDetail.Add(detailHeader);

            var dot = new VisualElement();
            dot.AddToClassList("molca-hub-bv-detail-dot");
            detailHeader.Add(dot);

            var nameLabel = new Label(profile.FindPropertyRelative("name").stringValue);
            nameLabel.AddToClassList("molca-hub-bv-detail-title");
            detailHeader.Add(nameLabel);

            var sub = new Label("profile");
            sub.AddToClassList("molca-hub-bv-detail-sub");
            detailHeader.Add(sub);

            var body = new VisualElement();
            body.AddToClassList("molca-hub-bv-detail-body");
            _profileDetail.Add(body);

            // Target / output / package override. The profile name shows in the detail header above
            // (and is edited from the rail), matching the design handoff which has no Name field here.
            body.Add(BuildProfileField(profile, "target", "Target"));
            body.Add(BuildProfileField(profile, "outputPath", "Output Path"));
            body.Add(BuildProfileField(profile, "applicationIdentifierOverride", "Package Name Override"));

            body.Add(BuildConfigurationCard(profile));
            body.Add(BuildOptionsCard(profile));
            body.Add(BuildPlatformSigningCard(profile));
            body.Add(BuildProfileActions(profile));
        }

        private VisualElement BuildConfigurationCard(SerializedProperty profile)
        {
            var card = MakeCard("Configuration");
            card.body.Add(BuildProfileField(profile, "runtimeManager", "Runtime Manager"));
            card.body.Add(BuildProfileField(profile, "globalSettings", "Global Settings"));
            return card.root;
        }

        private VisualElement BuildOptionsCard(SerializedProperty profile)
        {
            var card = MakeCard("Build Options");

            var grid = new VisualElement();
            grid.AddToClassList("molca-hub-bv-options-grid");
            card.body.Add(grid);

            grid.Add(BuildOptionGroup("Development", profile,
                ("developmentBuild", "Development Build"),
                ("allowDebugging", "Allow Debugging")));
            grid.Add(BuildOptionGroup("Performance", profile,
                ("il2cpp", "IL2CPP"),
                ("compress", "Compress")));
            grid.Add(BuildOptionGroup("Build Behavior", profile,
                ("autoRunPlayer", "Auto Run Player"),
                ("showBuiltPlayer", "Show Built Player"),
                ("cleanBuildCache", "Clean Build Cache"),
                ("restoreOriginalTarget", "Restore Original Target")));
            grid.Add(BuildOptionGroup("Debugging", profile,
                ("connectWithProfiler", "Connect Profiler"),
                ("deepProfiling", "Deep Profiling")));
            grid.Add(BuildOptionGroup("Advanced", profile,
                ("strictMode", "Strict Mode"),
                ("detailedBuildReport", "Detailed Build Report")));
            grid.Add(BuildOptionGroup("Content", profile,
                ("buildAddressablesFirst", "Build Addressables First")));

            return card.root;
        }

        private VisualElement BuildOptionGroup(string title, SerializedProperty profile, params (string prop, string label)[] toggles)
        {
            var group = new VisualElement();
            group.AddToClassList("molca-hub-bv-option-group");

            var heading = new Label(title.ToUpperInvariant());
            heading.AddToClassList("molca-hub-bv-option-heading");
            group.Add(heading);

            foreach (var (prop, label) in toggles)
                group.Add(BuildToggleRow(profile, prop, label));

            return group;
        }

        private VisualElement BuildPlatformSigningCard(SerializedProperty profile)
        {
            var card = MakeCard("Platform & Signing");

            card.body.Add(BuildToggleRow(profile, "buildAppBundle", "Build App Bundle (AAB)"));
            card.body.Add(BuildProfileField(profile, "androidArchitectures", "Architectures"));

            var useSigning = profile.FindPropertyRelative("useCustomSigning");
            card.body.Add(BuildToggleRow(profile, "useCustomSigning", "Use Custom Signing"));

            var signing = new VisualElement();
            signing.AddToClassList("molca-hub-bv-signing");
            card.body.Add(signing);

            signing.Add(BuildProfileField(profile, "androidKeystorePath", "Keystore Path"));
            signing.Add(BuildProfileField(profile, "androidKeyaliasName", "Key Alias Name"));
            signing.Add(BuildProfileField(profile, "androidKeystorePassEnv", "Keystore Pass Env"));
            signing.Add(BuildProfileField(profile, "androidKeyaliasPassEnv", "Key Alias Pass Env"));
            signing.Add(BuildProfileField(profile, "iosTeamId", "Apple Team ID"));
            signing.Add(BuildToggleRow(profile, "iosAutomaticSigning", "iOS Automatic Signing"));

            var note = new Label("Passwords are read from the named environment variables at build time and are never stored in this asset.");
            note.AddToClassList("molca-hub-muted");
            signing.Add(note);

            void RefreshSigning() => signing.style.display = useSigning.boolValue ? DisplayStyle.Flex : DisplayStyle.None;
            RefreshSigning();
            // Track the toggle so the signing block reveals/hides without a full rebuild.
            signing.schedule.Execute(RefreshSigning).Every(200);

            card.body.Add(BuildProfileField(profile, "defineSymbols", "Define Symbols"));
            return card.root;
        }

        private VisualElement BuildProfileActions(SerializedProperty profile)
        {
            var actions = new VisualElement();
            actions.AddToClassList("molca-hub-bv-actions");

            var apply = new Button(() =>
            {
                var profileName = profile.FindPropertyRelative("name").stringValue;
                _buildSerialized.ApplyModifiedProperties();
                MolcaEditorPrefs.SetString(AppliedProfileKey, profileName);
                EditorApplication.delayCall += () =>
                {
                    BuildManager.ApplyProfile(profileName);
                    RebuildProfileRail();
                };
            })
            { text = "Apply" };
            apply.AddToClassList("molca-hub-bv-action");
            actions.Add(apply);

            var build = new Button(() =>
            {
                var profileName = profile.FindPropertyRelative("name").stringValue;
                _buildSerialized.ApplyModifiedProperties();
                EditorApplication.delayCall += () => BuildProfileGated(profileName);
            })
            { text = "Build This Profile" };
            build.AddToClassList("molca-hub-bv-action");
            build.AddToClassList("molca-hub-bv-action--primary");
            actions.Add(build);

            var duplicate = new Button(DuplicateSelectedProfile) { text = "Duplicate" };
            duplicate.AddToClassList("molca-hub-bv-action");
            actions.Add(duplicate);

            return actions;
        }

        // -------------------------------------------------------------------
        // Profile mutations (SerializedObject flow)
        // -------------------------------------------------------------------

        private void AddProfile()
        {
            int index = _profiles.arraySize;
            _profiles.InsertArrayElementAtIndex(index);
            var element = _profiles.GetArrayElementAtIndex(index);
            element.FindPropertyRelative("name").stringValue = "New Profile";
            element.FindPropertyRelative("target").enumValueIndex = (int)BuildTarget.StandaloneWindows64;
            element.FindPropertyRelative("outputPath").stringValue = "Builds";
            _buildSerialized.ApplyModifiedProperties();

            SelectProfile(index);
        }

        private void RemoveSelectedProfile()
        {
            if (_selectedProfileIndex < 0 || _selectedProfileIndex >= _profiles.arraySize)
                return;

            var name = _profiles.GetArrayElementAtIndex(_selectedProfileIndex).FindPropertyRelative("name").stringValue;
            if (!EditorUtility.DisplayDialog("Remove Profile", $"Remove build profile '{name}'?", "Remove", "Cancel"))
                return;

            _profiles.DeleteArrayElementAtIndex(_selectedProfileIndex);
            _buildSerialized.ApplyModifiedProperties();

            SelectProfile(Mathf.Clamp(_selectedProfileIndex, 0, _profiles.arraySize - 1));
        }

        private void DuplicateSelectedProfile()
        {
            if (_selectedProfileIndex < 0 || _selectedProfileIndex >= _profiles.arraySize)
                return;

            // InsertArrayElementAtIndex copies the element at the index, giving an exact duplicate.
            _profiles.InsertArrayElementAtIndex(_selectedProfileIndex);
            var duplicate = _profiles.GetArrayElementAtIndex(_selectedProfileIndex + 1);
            var nameProp = duplicate.FindPropertyRelative("name");
            nameProp.stringValue = $"{nameProp.stringValue} Copy";
            _buildSerialized.ApplyModifiedProperties();

            SelectProfile(_selectedProfileIndex + 1);
        }

        // -------------------------------------------------------------------
        // Version view
        // -------------------------------------------------------------------

        private void BuildVersionView()
        {
            _versionSerialized.Update();

            _viewContainer.Add(BuildVersionSummary());
            _viewContainer.Add(BuildVersionFieldsCard());
            _viewContainer.Add(BuildIncrementButtons());

            var warning = new VisualElement();
            warning.AddToClassList("molca-hub-bv-warning");
            var warnIcon = new Label("⚠");
            warnIcon.AddToClassList("molca-hub-bv-warning__icon");
            warning.Add(warnIcon);
            var warnText = new Label("Build number and changelog entries are only updated when a build runs (Build Manager).");
            warnText.AddToClassList("molca-hub-bv-warning__text");
            warning.Add(warnText);
            _viewContainer.Add(warning);

            _viewContainer.Add(BuildReleaseFoldout());
            _viewContainer.Add(BuildAdvancedFoldout());
            _viewContainer.Add(BuildHistoryFoldout());
        }

        private VisualElement BuildVersionSummary()
        {
            var summary = new VisualElement();
            summary.AddToClassList("molca-hub-bv-summary");

            var currentStack = new VisualElement();
            currentStack.AddToClassList("molca-hub-bv-summary__stack");
            summary.Add(currentStack);

            var currentLabel = new Label("CURRENT");
            currentLabel.AddToClassList("molca-hub-bv-summary__caption");
            currentStack.Add(currentLabel);

            var big = new Label(_versionSettings.GetFullVersionString());
            big.AddToClassList("molca-hub-bv-summary__value");
            currentStack.Add(big);

            var meta = new Label($"Version  {_versionSettings.GetVersionString()}      Build  {_versionSettings.GetBuildNumberString()}");
            meta.AddToClassList("molca-hub-bv-summary__meta");
            summary.Add(meta);

            return summary;
        }

        private VisualElement BuildVersionFieldsCard()
        {
            var card = MakeCard("Version Fields");

            var grid = new VisualElement();
            grid.AddToClassList("molca-hub-bv-version-grid");
            card.body.Add(grid);

            grid.Add(BuildVersionField("major", "Major"));
            grid.Add(BuildVersionField("minor", "Minor"));
            grid.Add(BuildVersionField("patch", "Patch"));
            grid.Add(BuildVersionField("buildNumber", "Build"));

            return card.root;
        }

        private VisualElement BuildVersionField(string propertyName, string label)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-field-row");
            row.AddToClassList("molca-hub-bv-version-field");

            var fieldLabel = new Label(label);
            fieldLabel.AddToClassList("molca-hub-field-label");
            fieldLabel.AddToClassList("molca-hub-bv-version-field__label");
            row.Add(fieldLabel);

            var property = _versionSerialized.FindProperty(propertyName);
            var field = new PropertyField(property, string.Empty);
            field.AddToClassList("molca-hub-field-control");
            field.BindProperty(property);
            row.Add(field);

            return row;
        }

        private VisualElement BuildIncrementButtons()
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-bv-actions");

            row.Add(MakeIncrementButton("Increment Patch", () =>
            {
                var patch = _versionSerialized.FindProperty("patch");
                patch.intValue++;
            }));
            row.Add(MakeIncrementButton("Increment Minor", () =>
            {
                _versionSerialized.FindProperty("minor").intValue++;
                _versionSerialized.FindProperty("patch").intValue = 0;
            }));
            row.Add(MakeIncrementButton("Increment Major", () =>
            {
                _versionSerialized.FindProperty("major").intValue++;
                _versionSerialized.FindProperty("minor").intValue = 0;
                _versionSerialized.FindProperty("patch").intValue = 0;
            }));

            return row;
        }

        private Button MakeIncrementButton(string text, Action mutate)
        {
            var button = new Button(() =>
            {
                _versionSerialized.Update();
                mutate();
                _versionSerialized.ApplyModifiedProperties();
                SelectView(VersionView); // refresh summary + bound fields
            })
            { text = text };
            button.AddToClassList("molca-hub-bv-action");
            return button;
        }

        private VisualElement BuildReleaseFoldout()
        {
            var foldout = new Foldout { text = "Release", value = false };
            foldout.AddToClassList("molca-hub-bv-foldout");

            var suggestLabel = new Label();
            suggestLabel.AddToClassList("molca-hub-muted");
            suggestLabel.style.display = DisplayStyle.None;

            ReleaseTool.BumpSuggestion? suggestion = null;

            var suggest = new Button(() =>
            {
                suggestion = ReleaseTool.SuggestBump();
                var since = string.IsNullOrEmpty(suggestion.Value.SinceRef) ? "recent history" : suggestion.Value.SinceRef;
                suggestLabel.text = $"Suggested: {suggestion.Value.Bump} ({suggestion.Value.Commits.Count} commits since {since})";
                suggestLabel.style.display = DisplayStyle.Flex;
            })
            { text = "Suggest Bump From Commits" };
            suggest.AddToClassList("molca-hub-action-full");
            foldout.Add(suggest);
            foldout.Add(suggestLabel);

            var applyBump = new Button(() =>
            {
                if (suggestion.HasValue && suggestion.Value.Bump != VersionBump.None)
                {
                    ReleaseTool.ApplyBump(_versionSettings, suggestion.Value.Bump);
                    suggestion = null;
                    suggestLabel.style.display = DisplayStyle.None;
                    SelectView(VersionView);
                }
            })
            { text = "Apply Suggested Bump" };
            applyBump.AddToClassList("molca-hub-action-full");
            foldout.Add(applyBump);

            var createTag = new Toggle($"Create git tag (v{_versionSettings.GetVersionString()})") { value = false };
            foldout.Add(createTag);

            var release = new Button(() =>
            {
                var confirm = EditorUtility.DisplayDialog("Create Release",
                    $"Release v{_versionSettings.GetVersionString()}? This syncs PlayerSettings and appends a changelog entry" +
                    (createTag.value ? ", then creates a local git tag (not pushed)." : "."),
                    "Release", "Cancel");
                if (!confirm) return;

                var result = ReleaseTool.CreateRelease(_versionSettings, createTag.value);
                EditorUtility.DisplayDialog(result.Success ? "Release" : "Release Failed", result.Message, "OK");
                SelectView(VersionView);
            })
            { text = $"Create Release v{_versionSettings.GetVersionString()}" };
            release.AddToClassList("molca-hub-action-full");
            release.AddToClassList("molca-hub-action-full--primary");
            foldout.Add(release);

            return foldout;
        }

        private VisualElement BuildAdvancedFoldout()
        {
            var foldout = new Foldout { text = "Advanced", value = false };
            foldout.AddToClassList("molca-hub-bv-foldout");

            foldout.Add(BuildVersionPropertyField("autoSync", "Auto-Sync"));
            foldout.Add(BuildVersionPropertyField("autoIncrementBuildNumberOnBuild", "Auto Increment Build"));
            foldout.Add(BuildVersionPropertyField("autoAppendChangelogOnBuild", "Auto Changelog"));
            foldout.Add(BuildVersionPropertyField("changelogPath", "Changelog Path"));
            foldout.Add(BuildVersionPropertyField("includeGitCommitsInChangelog", "Include Git Commits"));
            foldout.Add(BuildVersionPropertyField("preReleaseIdentifier", "Pre-release"));
            foldout.Add(BuildVersionPropertyField("buildMetadata", "Build Metadata"));

            var sync = new Button(() =>
            {
                _versionSettings.SyncToUnityPlayerSettings(force: true);
                EditorUtility.SetDirty(_versionSettings);
            })
            { text = "Sync Now" };
            sync.AddToClassList("molca-hub-action-full");
            foldout.Add(sync);

            return foldout;
        }

        private VisualElement BuildHistoryFoldout()
        {
            var foldout = new Foldout { text = "History", value = false };
            foldout.AddToClassList("molca-hub-bv-foldout");

            var source = new Label($"Loaded from: {_versionSettings.ChangelogPath}");
            source.AddToClassList("molca-hub-muted");
            foldout.Add(source);

            var history = _versionSettings.GetVersionHistory();
            if (history.Length == 0)
            {
                var empty = new Label("No history entries.");
                empty.AddToClassList("molca-hub-muted");
                foldout.Add(empty);
                return foldout;
            }

            int startIndex = Mathf.Max(0, history.Length - 5);
            for (int i = startIndex; i < history.Length; i++)
            {
                var entry = history[i];
                var line = new Label($"v{entry.version} • {entry.timestamp} • {entry.changeType}");
                line.AddToClassList("molca-hub-bv-history-entry");
                foldout.Add(line);
                if (!string.IsNullOrEmpty(entry.notes))
                {
                    var notes = new Label(entry.notes);
                    notes.AddToClassList("molca-hub-muted");
                    foldout.Add(notes);
                }
            }

            return foldout;
        }

        private VisualElement BuildVersionPropertyField(string propertyName, string label)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-field-row");
            row.Add(BuildFieldLabel(label));

            var property = _versionSerialized.FindProperty(propertyName);
            var field = new PropertyField(property, string.Empty);
            field.AddToClassList("molca-hub-field-control");
            field.BindProperty(property);
            row.Add(field);

            return row;
        }

        // -------------------------------------------------------------------
        // Footer + shared helpers
        // -------------------------------------------------------------------

        private void BuildFooter()
        {
            var footer = new VisualElement();
            footer.AddToClassList("molca-hub-bv-footer");
            Add(footer);

            var sync = new Button(() =>
            {
                _versionSettings.SyncToUnityPlayerSettings(force: true);
                EditorUtility.SetDirty(_versionSettings);
            })
            { text = "Sync to Player Settings", tooltip = "Write the current version to Unity PlayerSettings." };
            sync.AddToClassList("molca-hub-bv-footer__button");
            footer.Add(sync);

            var note = new Label($"PlayerSettings version: {PlayerSettings.bundleVersion}");
            note.AddToClassList("molca-hub-bv-footer__note");
            footer.Add(note);
        }

        private VisualElement BuildProfileField(SerializedProperty profile, string relativeName, string label)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-field-row");
            row.AddToClassList("molca-hub-bv-field");

            row.Add(BuildFieldLabel(label));

            var property = profile.FindPropertyRelative(relativeName);
            var field = new PropertyField(property, string.Empty);
            field.AddToClassList("molca-hub-field-control");
            field.BindProperty(property);
            row.Add(field);

            return row;
        }

        private VisualElement BuildToggleRow(SerializedProperty profile, string relativeName, string label)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-bv-toggle-row");

            var text = new Label(label);
            text.AddToClassList("molca-hub-bv-toggle-label");
            row.Add(text);

            var property = profile.FindPropertyRelative(relativeName);
            var toggle = new Toggle();
            toggle.AddToClassList("molca-hub-bv-toggle");
            toggle.BindProperty(property);
            row.Add(toggle);

            return row;
        }

        private static (VisualElement root, VisualElement body) MakeCard(string title)
        {
            var root = new VisualElement();
            root.AddToClassList("molca-hub-bv-card");

            var header = new Label(title.ToUpperInvariant());
            header.AddToClassList("molca-hub-bv-card__header");
            root.Add(header);

            var body = new VisualElement();
            body.AddToClassList("molca-hub-bv-card__body");
            root.Add(body);

            return (root, body);
        }

        private static Label BuildFieldLabel(string text)
        {
            var label = new Label(text);
            label.AddToClassList("molca-hub-field-label");
            return label;
        }

        private int ResolveSelectedProfileIndex(string profileName)
        {
            if (_profiles.arraySize == 0)
                return -1;

            if (!string.IsNullOrEmpty(profileName))
            {
                for (int i = 0; i < _profiles.arraySize; i++)
                {
                    if (string.Equals(_profiles.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue,
                            profileName, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }

            return 0;
        }

        /// <summary>
        /// True when <paramref name="profile"/> is the one last applied from the Hub <em>and</em> its
        /// config still matches the live PlayerSettings. Clicking Apply writes those settings, so the
        /// badge marks the profile that genuinely reflects the current editor target/backend/defines.
        /// </summary>
        private bool ProfileIsActive(BuildSettings.BuildProfile profile)
        {
            if (profile == null)
                return false;

            var applied = MolcaEditorPrefs.GetString(AppliedProfileKey, string.Empty);
            if (!string.Equals(profile.name, applied, StringComparison.OrdinalIgnoreCase))
                return false;

            return ProfileMatchesPlayerSettings(profile);
        }

        private static bool ProfileMatchesPlayerSettings(BuildSettings.BuildProfile profile)
        {
            if (EditorUserBuildSettings.activeBuildTarget != profile.target)
                return false;

            var named = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(profile.target));

            var expectedBackend = profile.il2cpp ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x;
            if (PlayerSettings.GetScriptingBackend(named) != expectedBackend)
                return false;

            // Empty profile defines are not written by ApplyProfile, so they impose no constraint.
            if (!string.IsNullOrWhiteSpace(profile.defineSymbols))
            {
                var current = PlayerSettings.GetScriptingDefineSymbols(named);
                if (!string.Equals((current ?? string.Empty).Trim(), profile.defineSymbols.Trim(), StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        private static string ShortTarget(BuildTarget target)
        {
            return target switch
            {
                BuildTarget.StandaloneWindows64 => "Win64",
                BuildTarget.StandaloneWindows => "Win",
                BuildTarget.StandaloneOSX => "macOS",
                BuildTarget.StandaloneLinux64 => "Linux64",
                BuildTarget.Android => "Android",
                BuildTarget.iOS => "iOS",
                BuildTarget.WebGL => "WebGL",
                _ => target.ToString()
            };
        }

        private void BuildAllForActiveTarget()
        {
            var activeTarget = EditorUserBuildSettings.activeBuildTarget;
            var matching = new System.Collections.Generic.List<string>();
            var skipped = new System.Collections.Generic.List<string>();

            foreach (var profile in _buildSettings.Profiles)
            {
                if (profile == null || string.IsNullOrWhiteSpace(profile.name))
                    continue;
                if (profile.target == activeTarget)
                    matching.Add(profile.name);
                else
                    skipped.Add($"{profile.name} ({profile.target})");
            }

            if (matching.Count == 0)
            {
                EditorUtility.DisplayDialog("Build All",
                    $"No profiles target the active build target ({activeTarget}).", "OK");
                return;
            }

            var message = $"Build {matching.Count} profile(s) for {activeTarget}?";
            if (skipped.Count > 0)
            {
                message += $"\n\n{skipped.Count} profile(s) targeting other platforms will be skipped — the " +
                    "editor builds one target at a time; use CI for multi-target builds:\n  " + string.Join("\n  ", skipped);
            }

            if (!EditorUtility.DisplayDialog("Build All", message, "Build All", "Cancel"))
                return;

            var names = matching.ToArray();
            EditorApplication.delayCall += () => BuildAllGated(names);
        }

        // async void is the Unity event-handler entry-point exception in the async contract; the body
        // is wrapped so exceptions cannot escape into Unity's synchronization context.
        private static async void BuildProfileGated(string profileName)
        {
            try { await BuildManager.BuildAsync(profileName); }
            catch (Exception e) { Debug.LogError($"[BuildManager] Build failed: {e}"); }
        }

        private static async void BuildAllGated(string[] profileNames)
        {
            try
            {
                for (int i = 0; i < profileNames.Length; i++)
                {
                    var report = await BuildManager.BuildAsync(profileNames[i], runPreBuildChecks: i == 0);
                    if (i == 0 && report == null)
                    {
                        Debug.LogWarning("[BuildManager] Build All aborted (pre-build checks failed or the first build did not run).");
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[BuildManager] Build All failed: {e}");
            }
        }
    }
}
