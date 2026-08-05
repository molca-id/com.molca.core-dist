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
        private IVisualElementScheduledItem _refreshPoll;
        private Label _activeTargetLabel;
        private Label _headerVersionLabel;
        private Label _summaryVersionLabel;
        private Label _summaryMetaLabel;
        private Label _playerSettingsVersionLabel;
        private Button _buildAllButton;
        private Button _releaseButton;
        private VisualElement _invalidVersionNotice;
        private VisualElement _outcomeStrip;
        private Label _outcomeLabel;

        private int _selectedProfileIndex;

        /// <summary>
        /// What the last build started from this Hub did, so the section can say so.
        /// </summary>
        /// <remarks>
        /// Static because the build outlives the view: a build takes minutes, during which the section
        /// may be rebuilt (a tab switch, a domain reload from a target switch) and the instance that
        /// started it is gone. Until this existed, the section dispatched a multi-minute operation and
        /// then reported nothing at all — the outcome went to the console, which is the surface the Hub
        /// exists to replace. A build that had silently aborted in a pre-build gate looked, from here,
        /// exactly like one that had never been clicked.
        /// </remarks>
        private static BuildOutcome _lastOutcome;

        /// <summary>The result of one build attempt, for display.</summary>
        private readonly struct BuildOutcome
        {
            public string Profile { get; }
            public bool Succeeded { get; }
            public string Detail { get; }
            public System.DateTime FinishedAt { get; }

            public BuildOutcome(string profile, bool succeeded, string detail)
            {
                Profile = profile;
                Succeeded = succeeded;
                Detail = detail;
                FinishedAt = System.DateTime.Now;
            }
        }

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

            RegisterCallback<AttachToPanelEvent>(_ =>
                _refreshPoll = schedule.Execute(RefreshDynamicLabels).Every(250));
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                _refreshPoll?.Pause();
                _refreshPoll = null;
            });
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

            _activeTargetLabel = new Label();
            _activeTargetLabel.AddToClassList("molca-hub-bv-context__meta");
            header.Add(_activeTargetLabel);

            _headerVersionLabel = new Label();
            _headerVersionLabel.AddToClassList("molca-hub-bv-context__meta");
            header.Add(_headerVersionLabel);

            var spacer = new VisualElement();
            spacer.AddToClassList("molca-hub-spacer");
            header.Add(spacer);

            _buildAllButton = new Button(BuildAllForActiveTarget)
            {
                text = "Build All",
                tooltip = string.Empty
            };
            _buildAllButton.AddToClassList("molca-hub-bv-primary-pill");
            header.Add(_buildAllButton);

            RefreshDynamicLabels();
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

            // Elements owned by the outgoing view are gone; drop the handles so the refresh loop does
            // not style a detached element.
            _viewContainer.Clear();
            _invalidVersionNotice = null;

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
            card.body.Add(BuildToggleRow(profile, "useCustomSigning", "Use Custom Signing", out var signingToggle));

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
            // Driven by the toggle, not by a second timer. This used to poll every 200ms alongside the
            // section's own 250ms label poll — two independent clocks in one view, to reveal a panel
            // whose one trigger is sitting right there.
            signingToggle.RegisterValueChangedCallback(_ => RefreshSigning());

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
            // intValue, not enumValueIndex: BuildTarget is non-contiguous, so enumValueIndex is a
            // position in the popup list rather than the enum's value. Assigning
            // (int)StandaloneWindows64 = 19 to it selected whatever happens to be declared 20th in
            // UnityEditor.BuildTarget — a retired console — and every new profile started life
            // pointing at it. The rail already reads the live object for exactly this reason.
            element.FindPropertyRelative("target").intValue = (int)BuildTarget.StandaloneWindows64;
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
            _viewContainer.Add(BuildInvalidVersionNotice());
            _viewContainer.Add(BuildVersionFieldsCard());
            _viewContainer.Add(BuildIncrementButtons());

            var warning = new VisualElement();
            warning.AddToClassList("molca-hub-bv-warning");
            var warnIcon = new Label("⚠");
            warnIcon.AddToClassList("molca-hub-bv-warning__icon");
            warning.Add(warnIcon);
            var warnText = new Label(
                "The build number advances and a changelog entry is written after a build succeeds — " +
                "never for a build that failed or was aborted by a pre-build gate.");
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

            _summaryVersionLabel = new Label();
            _summaryVersionLabel.AddToClassList("molca-hub-bv-summary__value");
            currentStack.Add(_summaryVersionLabel);

            _summaryMetaLabel = new Label();
            _summaryMetaLabel.AddToClassList("molca-hub-bv-summary__meta");
            summary.Add(_summaryMetaLabel);

            RefreshDynamicLabels();

            return summary;
        }

        /// <summary>
        /// The warning shown when the authored version cannot produce a build.
        /// </summary>
        /// <remarks>
        /// The Inspector this view replaced said so; this one did not, so the only surface that told you
        /// a build number of 0 was invalid was the one the project was moving away from. The same
        /// condition is a Doctor error (<c>version-settings-valid</c>) and therefore aborts the build —
        /// finding that out at the point of authoring beats finding it out minutes into a build.
        /// </remarks>
        private VisualElement BuildInvalidVersionNotice()
        {
            var notice = new VisualElement();
            notice.AddToClassList("molca-hub-bv-warning");

            var icon = new Label("⚠");
            icon.AddToClassList("molca-hub-bv-warning__icon");
            notice.Add(icon);

            var text = new Label(
                "Version is invalid: Major, Minor and Patch must be zero or greater, and Build must be " +
                "at least 1. Builds abort until this is fixed.");
            text.AddToClassList("molca-hub-bv-warning__text");
            notice.Add(text);

            _invalidVersionNotice = notice;
            RefreshInvalidVersionNotice();
            return notice;
        }

        private void RefreshInvalidVersionNotice()
        {
            if (_invalidVersionNotice == null)
                return;

            _invalidVersionNotice.style.display =
                _versionSettings.IsValidVersion() ? DisplayStyle.None : DisplayStyle.Flex;
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
            field.RegisterCallback<SerializedPropertyChangeEvent>(_ => RefreshDynamicLabels());
            row.Add(field);

            return row;
        }

        /// <summary>
        /// The three bump buttons, each delegating to <see cref="VersionSettings"/>'s own increment.
        /// </summary>
        /// <remarks>
        /// These used to reproduce the SemVer reset rules in raw <see cref="SerializedProperty"/>
        /// arithmetic — a second implementation of <c>IncrementMinor</c>/<c>IncrementMajor</c> that
        /// happened to agree with the model, and that nothing would have caught if it stopped agreeing.
        /// One surface, one implementation.
        /// </remarks>
        private VisualElement BuildIncrementButtons()
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-bv-actions");

            row.Add(MakeIncrementButton("Increment Patch", v => v.IncrementPatch()));
            row.Add(MakeIncrementButton("Increment Minor", v => v.IncrementMinor()));
            row.Add(MakeIncrementButton("Increment Major", v => v.IncrementMajor()));

            return row;
        }

        private Button MakeIncrementButton(string text, Action<VersionSettings> mutate)
        {
            var button = new Button(() =>
            {
                // Flush in-flight field edits to the asset first: the increments below operate on the
                // object, and an unapplied SerializedObject edit would be overwritten by the Update
                // afterwards rather than counted.
                _versionSerialized.ApplyModifiedProperties();

                Undo.RecordObject(_versionSettings, text);
                mutate(_versionSettings);
                EditorUtility.SetDirty(_versionSettings);

                _versionSerialized.Update();
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

            _releaseButton = new Button(() =>
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
            { text = string.Empty };
            _releaseButton.AddToClassList("molca-hub-action-full");
            _releaseButton.AddToClassList("molca-hub-action-full--primary");
            foldout.Add(_releaseButton);
            RefreshDynamicLabels();

            return foldout;
        }

        private VisualElement BuildAdvancedFoldout()
        {
            var foldout = new Foldout { text = "Advanced", value = false };
            foldout.AddToClassList("molca-hub-bv-foldout");

            foldout.Add(BuildVersionPropertyField("autoIncrementBuildNumberOnBuild", "Auto Increment Build"));
            foldout.Add(BuildVersionPropertyField("autoAppendChangelogOnBuild", "Auto Changelog"));
            foldout.Add(BuildVersionPropertyField("changelogPath", "Changelog Path"));
            foldout.Add(BuildVersionPropertyField("includeGitCommitsInChangelog", "Include Git Commits"));
            foldout.Add(BuildVersionPropertyField("preReleaseIdentifier", "Pre-release"));
            foldout.Add(BuildVersionPropertyField("buildMetadata", "Build Metadata"));

            var sync = new Button(() =>
            {
                _versionSettings.SyncToUnityPlayerSettings();
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
            field.RegisterCallback<SerializedPropertyChangeEvent>(_ => RefreshDynamicLabels());
            row.Add(field);

            return row;
        }

        // -------------------------------------------------------------------
        // Footer + shared helpers
        // -------------------------------------------------------------------

        /// <summary>The strip that reports what the last build from this Hub did.</summary>
        private VisualElement BuildOutcomeStrip()
        {
            _outcomeStrip = new VisualElement();
            _outcomeStrip.AddToClassList("molca-hub-bv-warning");

            _outcomeLabel = new Label();
            _outcomeLabel.AddToClassList("molca-hub-bv-warning__text");
            _outcomeStrip.Add(_outcomeLabel);

            RefreshOutcomeStrip();
            return _outcomeStrip;
        }

        private void RefreshOutcomeStrip()
        {
            if (_outcomeStrip == null)
                return;

            var outcome = _lastOutcome;
            if (string.IsNullOrEmpty(outcome.Profile))
            {
                _outcomeStrip.style.display = DisplayStyle.None;
                return;
            }

            _outcomeStrip.style.display = DisplayStyle.Flex;
            _outcomeLabel.text =
                $"{(outcome.Succeeded ? "✓" : "✕")}  {outcome.Profile} · " +
                $"{outcome.FinishedAt:HH:mm:ss} · {outcome.Detail}";
        }

        /// <summary>Records an outcome for the strip to report.</summary>
        /// <param name="profile">The profile that was built.</param>
        /// <param name="succeeded">Whether the build produced a player.</param>
        /// <param name="detail">One line saying what happened.</param>
        private static void RecordOutcome(string profile, bool succeeded, string detail) =>
            _lastOutcome = new BuildOutcome(profile, succeeded, detail);

        private void BuildFooter()
        {
            Add(BuildOutcomeStrip());

            var footer = new VisualElement();
            footer.AddToClassList("molca-hub-bv-footer");
            Add(footer);

            var sync = new Button(() =>
            {
                _versionSettings.SyncToUnityPlayerSettings();
                EditorUtility.SetDirty(_versionSettings);
                RefreshDynamicLabels();
            })
            { text = "Sync to Player Settings", tooltip = "Write the current version to Unity PlayerSettings." };
            sync.AddToClassList("molca-hub-bv-footer__button");
            footer.Add(sync);

            _playerSettingsVersionLabel = new Label();
            _playerSettingsVersionLabel.AddToClassList("molca-hub-bv-footer__note");
            footer.Add(_playerSettingsVersionLabel);

            RefreshDynamicLabels();
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
            field.RegisterCallback<SerializedPropertyChangeEvent>(_ =>
            {
                _buildSerialized.ApplyModifiedProperties();
                RebuildProfileRail();
                RefreshDynamicLabels();
            });
            row.Add(field);

            return row;
        }

        private VisualElement BuildToggleRow(SerializedProperty profile, string relativeName, string label) =>
            BuildToggleRow(profile, relativeName, label, out _);

        /// <summary>Builds a labelled toggle row, exposing the toggle for callers that must react to it.</summary>
        /// <param name="profile">The profile element being edited.</param>
        /// <param name="relativeName">The boolean property's name within the profile.</param>
        /// <param name="label">The row's label.</param>
        /// <param name="toggle">The created toggle.</param>
        /// <returns>The row.</returns>
        private VisualElement BuildToggleRow(
            SerializedProperty profile, string relativeName, string label, out Toggle toggle)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-bv-toggle-row");

            var text = new Label(label);
            text.AddToClassList("molca-hub-bv-toggle-label");
            row.Add(text);

            var property = profile.FindPropertyRelative(relativeName);
            toggle = new Toggle();
            toggle.AddToClassList("molca-hub-bv-toggle");
            toggle.BindProperty(property);
            toggle.RegisterCallback<SerializedPropertyChangeEvent>(_ =>
            {
                _buildSerialized.ApplyModifiedProperties();
                RebuildProfileRail();
                RefreshDynamicLabels();
            });
            row.Add(toggle);

            return row;
        }

        /// <summary>
        /// Refreshes the labels that read state this view does not own — the active build target,
        /// PlayerSettings, and the last build's outcome.
        /// </summary>
        /// <remarks>
        /// Deliberately does not call <see cref="SerializedObject.Update"/>. It used to, on a 250 ms
        /// timer, which meant a periodic overwrite of both serialized objects while their fields were
        /// bound and possibly mid-edit. Everything here reads the live objects, which the binding system
        /// already keeps current, so the poll only exists for the external state above.
        /// </remarks>
        private void RefreshDynamicLabels()
        {
            RefreshInvalidVersionNotice();
            RefreshOutcomeStrip();

            if (_activeTargetLabel != null)
                _activeTargetLabel.text = $"active target  {EditorUserBuildSettings.activeBuildTarget}";

            if (_headerVersionLabel != null)
                _headerVersionLabel.text = $"· v{_versionSettings.GetFullVersionString()}";

            if (_buildAllButton != null)
                _buildAllButton.tooltip = $"Build every profile targeting {EditorUserBuildSettings.activeBuildTarget}.";

            if (_summaryVersionLabel != null)
                _summaryVersionLabel.text = _versionSettings.GetFullVersionString();

            if (_summaryMetaLabel != null)
                _summaryMetaLabel.text = $"Version  {_versionSettings.GetVersionString()}      Build  {_versionSettings.GetBuildNumberString()}";

            if (_releaseButton != null)
                _releaseButton.text = $"Create Release v{_versionSettings.GetVersionString()}";

            if (_playerSettingsVersionLabel != null)
                _playerSettingsVersionLabel.text = $"PlayerSettings version: {PlayerSettings.bundleVersion}";
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
            try
            {
                var report = await BuildManager.BuildAsync(profileName);
                RecordOutcome(profileName, DescribeReport(report, out var detail), detail);
            }
            catch (Exception e)
            {
                Debug.LogError($"[BuildManager] Build failed: {e}");
                RecordOutcome(profileName, false, $"failed: {e.Message}");
            }
        }

        private static async void BuildAllGated(string[] profileNames)
        {
            try
            {
                for (int i = 0; i < profileNames.Length; i++)
                {
                    var report = await BuildManager.BuildAsync(profileNames[i], runPreBuildChecks: i == 0);
                    RecordOutcome(profileNames[i], DescribeReport(report, out var detail), detail);

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
                RecordOutcome(profileNames.Length > 0 ? profileNames[0] : "Build All", false, $"failed: {e.Message}");
            }
        }

        /// <summary>
        /// Turns a build report into the one line the outcome strip shows.
        /// </summary>
        /// <param name="report">The report, or null when the build never ran.</param>
        /// <param name="detail">The line to display.</param>
        /// <returns>True when the build produced a player.</returns>
        /// <remarks>
        /// A null report is the case worth naming: the build was refused by the gate or a pre-build
        /// step, or was deferred across a build-target switch. "Nothing happened" and "it failed" want
        /// different next actions from the reader, so they get different text.
        /// </remarks>
        private static bool DescribeReport(UnityEditor.Build.Reporting.BuildReport report, out string detail)
        {
            if (report == null)
            {
                detail = "did not run — see the Console for the gate, step, or target-switch reason.";
                return false;
            }

            var summary = report.summary;
            if (summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                detail = $"built in {summary.totalTime.TotalSeconds:F0}s · " +
                         $"{summary.totalSize / 1024f / 1024f:F1} MB · {summary.outputPath}";
                return true;
            }

            detail = $"{summary.result} · {summary.totalErrors} error(s)";
            return false;
        }
    }
}
