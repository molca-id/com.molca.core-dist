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
        private VisualElement _preflightPanel;
        private Button _preflightButton;
        private VisualElement _historyList;
        private Label _sceneSourceNote;

        private int _selectedProfileIndex;

        /// <summary>
        /// The most recent build attempt's identity, so the refresh loop can tell a new one from a
        /// re-render of the same one and rebuild the history list only when it changes.
        /// </summary>
        /// <remarks>
        /// The outcome itself is no longer held here at all. It used to live in a <c>static</c> field,
        /// which the domain reload caused by <em>Restore Original Target</em> — on by default — discarded
        /// moments after the build recorded it, so the surface added to stop builds reporting nothing
        /// reported nothing in the most common configuration. <see cref="MolcaBuildRecordStore"/> persists
        /// attempts outside the domain, and this view reads them.
        /// </remarks>
        private string _renderedRecordStamp;
        private string _pendingLabel;
        private System.DateTime _pendingSinceUtc;

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

            // Assign stable ids to any profile authored before they existed. Done here, in the authoring
            // surface, because it writes to the asset — see BuildSettings.EnsureIds for why not OnEnable.
            if (_buildSettings.EnsureIds())
            {
                EditorUtility.SetDirty(_buildSettings);
                AssetDatabase.SaveAssets();
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

            // The footer survives the swap but reports on what the view just rebuilt — including whether
            // PlayerSettings still matches the asset, which an increment button changes by way of this
            // method. Without this the mirror notice stayed as it was until something else refreshed it.
            RefreshDynamicLabels();
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
            // Changing the target rebuilds this pane, because which platform fields apply depends on it.
            body.Add(BuildProfileField(profile, "target", "Target", rebuildsDetail: true));
            body.Add(BuildProfileField(profile, "outputPath", "Output Path"));
            body.Add(BuildProfileField(profile, "applicationIdentifierOverride", "Package Name Override"));

            body.Add(BuildScenesCard(profile));
            body.Add(BuildConfigurationCard(profile));
            body.Add(BuildOptionsCard(profile));
            body.Add(BuildPlatformSigningCard(profile));
            body.Add(BuildProfileActions(profile));
            body.Add(BuildPreflightPanel());
        }

        /// <summary>
        /// The profile's scene set, and what an empty list means.
        /// </summary>
        /// <remarks>
        /// Every profile used to build the one global enabled Build Settings list, so a development profile
        /// and a production profile could not ship different scenes. An empty list still means "use the
        /// Build Settings list", because that is what every existing profile does and silently switching
        /// them to "no scenes at all" would produce empty players.
        /// </remarks>
        private VisualElement BuildScenesCard(SerializedProperty profile)
        {
            var card = MakeCard("Scenes");

            var scenes = profile.FindPropertyRelative("scenes");
            var field = new PropertyField(scenes, "Scenes");
            field.BindProperty(scenes);
            field.RegisterCallback<SerializedPropertyChangeEvent>(_ =>
            {
                _buildSerialized.ApplyModifiedProperties();
                RefreshSceneSourceNote();
            });
            card.body.Add(field);

            _sceneSourceNote = new Label();
            _sceneSourceNote.AddToClassList("molca-hub-muted");
            card.body.Add(_sceneSourceNote);
            RefreshSceneSourceNote();

            return card.root;
        }

        private void RefreshSceneSourceNote()
        {
            if (_sceneSourceNote == null)
                return;

            var profile = SelectedProfile();
            if (profile == null)
                return;

            if (profile.HasSceneOverride)
            {
                _sceneSourceNote.text =
                    $"This profile builds the {profile.scenes.Count} scene(s) listed above, in order. " +
                    "The Editor Build Settings list is ignored.";
            }
            else
            {
                var enabled = SceneReferenceBuildValidator.EnabledBuildScenes().Count;
                _sceneSourceNote.text =
                    $"Empty — this profile builds the {enabled} enabled Editor Build Settings scene(s). " +
                    "Add scenes here to give this profile its own set.";
            }
        }

        /// <summary>The live profile object for the selected row, or null.</summary>
        private BuildSettings.BuildProfile SelectedProfile() =>
            _selectedProfileIndex >= 0 && _selectedProfileIndex < _buildSettings.Profiles.Count
                ? _buildSettings.Profiles[_selectedProfileIndex]
                : null;

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

        /// <summary>
        /// Platform-specific options and signing for the selected profile's target.
        /// </summary>
        /// <remarks>
        /// <b>Only the fields that apply to this profile's target are shown.</b> Every field used to be
        /// shown for every profile, so a Windows profile offered an Android app-bundle toggle and an Apple
        /// team ID, and an iOS profile offered a keystore path — none of which the build path reads for
        /// those targets. A form that presents settings which cannot take effect teaches people that
        /// filling it in does not mean anything.
        /// </remarks>
        private VisualElement BuildPlatformSigningCard(SerializedProperty profile)
        {
            var target = SelectedProfile()?.target ?? BuildTarget.StandaloneWindows64;
            bool isAndroid = target == BuildTarget.Android;
            bool isIos = target == BuildTarget.iOS;

            var card = MakeCard($"Platform & Signing · {ShortTarget(target)}");

            if (isAndroid)
            {
                card.body.Add(BuildToggleRow(profile, "buildAppBundle", "Build App Bundle (AAB)"));
                card.body.Add(BuildProfileField(profile, "androidArchitectures", "Architectures"));
            }

            if (isAndroid || isIos)
            {
                var useSigning = profile.FindPropertyRelative("useCustomSigning");
                card.body.Add(BuildToggleRow(profile, "useCustomSigning", "Use Custom Signing", out var signingToggle));

                var signing = new VisualElement();
                signing.AddToClassList("molca-hub-bv-signing");
                card.body.Add(signing);

                if (isAndroid)
                {
                    signing.Add(BuildProfileField(profile, "androidKeystorePath", "Keystore Path"));
                    signing.Add(BuildProfileField(profile, "androidKeyaliasName", "Key Alias Name"));
                    signing.Add(BuildProfileField(profile, "androidKeystorePassEnv", "Keystore Pass Env"));
                    signing.Add(BuildProfileField(profile, "androidKeyaliasPassEnv", "Key Alias Pass Env"));

                    var note = new Label(
                        "Passwords are read from the named environment variables at build time and are never " +
                        "stored in this asset. A build is refused — not signed with the debug keystore — when " +
                        "the keystore, alias or either variable is missing.");
                    note.AddToClassList("molca-hub-muted");
                    signing.Add(note);
                }
                else
                {
                    signing.Add(BuildProfileField(profile, "iosTeamId", "Apple Team ID"));
                    signing.Add(BuildToggleRow(profile, "iosAutomaticSigning", "iOS Automatic Signing"));
                }

                void RefreshSigning() => signing.style.display = useSigning.boolValue ? DisplayStyle.Flex : DisplayStyle.None;
                RefreshSigning();
                // Driven by the toggle, not by a second timer. This used to poll every 200ms alongside the
                // section's own 250ms label poll — two independent clocks in one view, to reveal a panel
                // whose one trigger is sitting right there.
                signingToggle.RegisterValueChangedCallback(_ => RefreshSigning());
            }
            else
            {
                var none = new Label(
                    $"{ShortTarget(target)} builds carry no Molca-managed signing configuration. " +
                    "Signing options appear for Android and iOS profiles.");
                none.AddToClassList("molca-hub-muted");
                card.body.Add(none);
            }

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
                MarkBuildPending($"{profileName} · running pre-build checks…");
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

        /// <summary>
        /// Runs the pre-build gate on demand and reports its findings here.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The gate already ran on every build; what it had no way of doing was telling you anything in the
        /// Hub. A refused build left this section saying only "did not run — see the Console for the gate,
        /// step, or target-switch reason", which sends the reader to the surface this window exists to
        /// replace, for the most consequential answer it has.
        /// </para>
        /// <para>
        /// It is also the same set of checks a build will run, deliberately: a preflight that tested
        /// something else would be a second opinion nobody asked for. <see cref="MolcaBuildGate"/> owns the
        /// list; this panel only displays it.
        /// </para>
        /// </remarks>
        private VisualElement BuildPreflightPanel()
        {
            var container = new VisualElement();

            _preflightButton = new Button(RunPreflight) { text = "Run Preflight Checks" };
            _preflightButton.AddToClassList("molca-hub-action-full");
            _preflightButton.tooltip =
                "Run the same Doctor checks a build runs, without building: " +
                string.Join(", ", MolcaBuildGate.CheckIds);
            container.Add(_preflightButton);

            _preflightPanel = new VisualElement();
            _preflightPanel.style.display = DisplayStyle.None;
            container.Add(_preflightPanel);

            return container;
        }

        // async void is the Unity event-handler entry-point exception in the async contract; the body is
        // wrapped so exceptions cannot escape into Unity's synchronization context.
        private async void RunPreflight()
        {
            var panelElement = _preflightPanel;
            var button = _preflightButton;
            if (panelElement == null)
                return;

            button?.SetEnabled(false);
            if (button != null)
                button.text = "Running preflight…";

            try
            {
                var result = await MolcaBuildGate.RunAsync();

                // The view may have been rebuilt (or the window closed) while the checks ran. Rendering
                // into a detached element would silently do nothing, so re-read the current handles.
                if (_preflightPanel != panelElement || panelElement.panel == null)
                    return;

                RenderPreflight(panelElement, result);
            }
            catch (System.OperationCanceledException)
            {
                // Cancellation is not an error; leave the panel as it was.
            }
            catch (Exception e)
            {
                Debug.LogError($"[MolcaHub] Preflight checks failed to run: {e}");
                if (_preflightPanel == panelElement && panelElement.panel != null)
                {
                    panelElement.Clear();
                    panelElement.style.display = DisplayStyle.Flex;
                    panelElement.Add(MakeFinding($"Preflight could not run: {e.Message}", isError: true));
                }
            }
            finally
            {
                if (_preflightButton == button && button != null)
                {
                    button.SetEnabled(true);
                    button.text = "Run Preflight Checks";
                }
            }
        }

        private void RenderPreflight(VisualElement panelElement, MolcaBuildGate.Result result)
        {
            panelElement.Clear();
            panelElement.style.display = DisplayStyle.Flex;

            if (result.Passed && result.Warnings.Count == 0)
            {
                panelElement.Add(MakeFinding(
                    $"✓ Preflight passed — {MolcaBuildGate.CheckIds.Count} build check(s), nothing to report.",
                    isError: false));
                return;
            }

            var heading = new Label(result.Passed
                ? $"Preflight passed with {result.Warnings.Count} warning(s) — a build would proceed."
                : $"Preflight failed: {result.Errors.Count} error(s) would abort a build.");
            heading.AddToClassList("molca-hub-bv-finding");
            heading.EnableInClassList("molca-hub-bv-finding--error", !result.Passed);
            panelElement.Add(heading);

            foreach (var issue in result.Errors)
                panelElement.Add(MakeFinding($"✕  [{issue.CheckId}] {issue.Message}", isError: true));

            foreach (var issue in result.Warnings)
                panelElement.Add(MakeFinding($"!  [{issue.CheckId}] {issue.Message}", isError: false));
        }

        private static Label MakeFinding(string text, bool isError)
        {
            var label = new Label(text);
            label.AddToClassList("molca-hub-bv-finding");
            label.EnableInClassList("molca-hub-bv-finding--error", isError);
            return label;
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
                suggestLabel.style.display = DisplayStyle.Flex;

                // A suggestion with no release tag to measure from is not "no changes" — it is "no
                // baseline". Saying so is the difference between a reading and a guess.
                if (!suggestion.Value.HasBaseline)
                {
                    suggestLabel.text =
                        "No v* release tag found, so there is no baseline to measure from. " +
                        $"({suggestion.Value.Commits.Count} recent commit(s) seen.) Pick the version " +
                        "yourself for the first release; suggestions work from the tag it creates.";
                    return;
                }

                suggestLabel.text = suggestion.Value.Bump == VersionBump.None
                    ? $"No version-affecting commits since {suggestion.Value.SinceRef} " +
                      $"({suggestion.Value.Commits.Count} commit(s) evaluated)."
                    : $"Suggested: {suggestion.Value.Bump} " +
                      $"({suggestion.Value.Commits.Count} commit(s) since {suggestion.Value.SinceRef})";
            })
            { text = "Suggest Bump From Commits" };
            suggest.AddToClassList("molca-hub-action-full");
            foldout.Add(suggest);
            foldout.Add(suggestLabel);

            var applyBump = new Button(() =>
            {
                // Silence was the old behaviour here: with nothing suggested, or a suggestion of None, the
                // click did nothing at all and looked identical to a click that had worked.
                if (!suggestion.HasValue)
                {
                    suggestLabel.style.display = DisplayStyle.Flex;
                    suggestLabel.text = "Nothing to apply — run Suggest Bump From Commits first.";
                    return;
                }

                if (!ReleaseTool.ApplyBump(_versionSettings, suggestion.Value.Bump))
                {
                    suggestLabel.style.display = DisplayStyle.Flex;
                    suggestLabel.text = suggestion.Value.HasBaseline
                        ? $"Nothing to apply — no version-affecting commits since {suggestion.Value.SinceRef}."
                        : "Nothing to apply — no release tag to measure from. Set the version by hand.";
                    return;
                }

                suggestion = null;
                suggestLabel.style.display = DisplayStyle.None;
                SelectView(VersionView);
            })
            { text = "Apply Suggested Bump" };
            applyBump.AddToClassList("molca-hub-action-full");
            foldout.Add(applyBump);

            // The release identity, not the numeric version: releasing 1.4.0-rc.1 must not offer to tag
            // v1.4.0 — that mislabels the candidate and spends the tag the real release needs.
            var createTag = new Toggle($"Create git tag (v{_versionSettings.GetReleaseVersionString()})") { value = false };
            foldout.Add(createTag);

            _releaseButton = new Button(() =>
            {
                var confirm = EditorUtility.DisplayDialog("Create Release",
                    $"Release v{_versionSettings.GetReleaseVersionString()}? This syncs PlayerSettings and appends a changelog entry" +
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

            // No second Sync button here. This one and the footer's did exactly the same thing, in one
            // view, and two buttons for one action invite the reader to look for the difference.
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

        /// <summary>
        /// The strip that reports what the last build did, from the persisted record.
        /// </summary>
        /// <remarks>
        /// Reads <see cref="MolcaBuildRecordStore"/> rather than a field this view wrote, so it reports
        /// builds started from CI, from the automation workflow, or from a Hub instance that a domain reload
        /// has since replaced — and survives that reload, which the field it replaced did not.
        /// </remarks>
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

        /// <summary>
        /// Says that a build has been dispatched, until a record newer than the dispatch appears.
        /// </summary>
        /// <param name="what">What is starting.</param>
        /// <remarks>
        /// This is as much progress reporting as the editor allows: <c>BuildPipeline.BuildPlayer</c> is
        /// synchronous and blocks the main thread, so no callback, repaint or cancel can happen while a
        /// build runs. What can be shown is the difference between "your click did nothing" and "the build
        /// is under way", which is the ambiguity worth removing — the asynchronous gate phase in particular
        /// can take a while before anything else happens.
        /// </remarks>
        private void MarkBuildPending(string what)
        {
            _pendingLabel = what;
            _pendingSinceUtc = System.DateTime.UtcNow;
            RefreshOutcomeStrip();
        }

        private void RefreshOutcomeStrip()
        {
            if (_outcomeStrip == null)
                return;

            var record = MolcaBuildRecordStore.Last;

            if (_pendingLabel != null)
            {
                // Cleared only by a record from after the dispatch: an older record is the previous build's.
                bool superseded = record != null &&
                    System.DateTime.TryParse(record.timestampUtc, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var recorded) &&
                    recorded.ToUniversalTime() >= _pendingSinceUtc;

                if (superseded)
                {
                    _pendingLabel = null;
                }
                else
                {
                    _outcomeStrip.style.display = DisplayStyle.Flex;
                    _outcomeLabel.text = $"⏳  {_pendingLabel}";
                    return;
                }
            }

            if (record == null)
            {
                _outcomeStrip.style.display = DisplayStyle.None;
                _renderedRecordStamp = null;
                return;
            }

            _outcomeStrip.style.display = DisplayStyle.Flex;
            _outcomeLabel.text = DescribeRecord(record);

            // Only rebuild the history list when the newest record actually changed; this runs on the
            // 250 ms refresh loop.
            var stamp = record.timestampUtc + record.profile + record.outcome;
            if (stamp != _renderedRecordStamp)
            {
                _renderedRecordStamp = stamp;
                RefreshHistoryList();
            }
        }

        /// <summary>One line describing a recorded build attempt.</summary>
        private static string DescribeRecord(MolcaBuildRecord record)
        {
            var mark = record.Outcome switch
            {
                MolcaBuildOutcome.Succeeded => "✓",
                MolcaBuildOutcome.Refused => "○",
                _ => "✕",
            };

            var version = string.IsNullOrEmpty(record.semanticVersion)
                ? string.Empty
                : $"v{record.semanticVersion} ({record.buildNumber}) · ";

            return $"{mark}  {record.profile} · {record.LocalTime:HH:mm:ss} · {version}{record.detail}";
        }

        /// <summary>
        /// The recent-build list: what was attempted, when, from which commit, and how it ended.
        /// </summary>
        private VisualElement BuildHistoryPanel()
        {
            var foldout = new Foldout { text = "Recent Builds", value = false };
            foldout.AddToClassList("molca-hub-bv-foldout");

            _historyList = new VisualElement();
            foldout.Add(_historyList);
            RefreshHistoryList();

            var clear = new Button(() =>
            {
                MolcaBuildRecordStore.Clear();
                _renderedRecordStamp = null;
                RefreshHistoryList();
                RefreshOutcomeStrip();
            })
            { text = "Clear History" };
            clear.AddToClassList("molca-hub-action-full");
            foldout.Add(clear);

            return foldout;
        }

        private void RefreshHistoryList()
        {
            if (_historyList == null)
                return;

            _historyList.Clear();

            var records = MolcaBuildRecordStore.Recent(10);
            if (records.Count == 0)
            {
                var empty = new Label("No builds recorded yet.");
                empty.AddToClassList("molca-hub-muted");
                _historyList.Add(empty);
                return;
            }

            foreach (var record in records)
            {
                var line = new Label(DescribeRecord(record));
                line.AddToClassList("molca-hub-bv-history-entry");
                line.EnableInClassList("molca-hub-bv-finding--error", record.Outcome == MolcaBuildOutcome.Failed);
                if (!string.IsNullOrEmpty(record.commit))
                    line.tooltip = $"{record.branch} @ {record.commit}\n{record.outputPath}";
                _historyList.Add(line);
            }
        }

        private void BuildFooter()
        {
            Add(BuildOutcomeStrip());
            Add(BuildHistoryPanel());

            var footer = new VisualElement();
            footer.AddToClassList("molca-hub-bv-footer");
            Add(footer);

            // Saves rather than only marking dirty: this button exists to make the Player inspector agree
            // with the Hub, and an agreement that a domain reload can drop is not one. The PlayerSettings
            // half is flushed by the same SaveAssets.
            var sync = new Button(() =>
            {
                _versionSettings.SyncToUnityPlayerSettings(EditorUserBuildSettings.activeBuildTarget);
                EditorUtility.SetDirty(_versionSettings);
                AssetDatabase.SaveAssets();
                RefreshDynamicLabels();
            })
            {
                text = "Sync to Player Settings",
                tooltip = "Write the current version and platform version code to Unity PlayerSettings.",
            };
            sync.AddToClassList("molca-hub-bv-footer__button");
            footer.Add(sync);

            _playerSettingsVersionLabel = new Label();
            _playerSettingsVersionLabel.AddToClassList("molca-hub-bv-footer__note");
            footer.Add(_playerSettingsVersionLabel);

            RefreshDynamicLabels();
        }

        /// <summary>Builds a labelled field bound to one property of the selected profile.</summary>
        /// <param name="profile">The profile element being edited.</param>
        /// <param name="relativeName">The property's name within the profile.</param>
        /// <param name="label">The row's label.</param>
        /// <param name="rebuildsDetail">
        /// True when changing this property changes which other fields apply, so the detail pane must be
        /// rebuilt. Deferred to the next editor tick: rebuilding the pane from inside a callback raised by
        /// an element in that same pane would destroy the element mid-event.
        /// </param>
        private VisualElement BuildProfileField(
            SerializedProperty profile, string relativeName, string label, bool rebuildsDetail = false)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-field-row");
            row.AddToClassList("molca-hub-bv-field");

            row.Add(BuildFieldLabel(label));

            var property = profile.FindPropertyRelative(relativeName);
            var field = new PropertyField(property, string.Empty);
            field.AddToClassList("molca-hub-field-control");
            field.BindProperty(property);

            var lastValue = PropertyStamp(property);
            field.RegisterCallback<SerializedPropertyChangeEvent>(_ =>
            {
                if (!ValueChanged(property, ref lastValue))
                    return;

                _buildSerialized.ApplyModifiedProperties();
                RebuildProfileRail();
                RefreshDynamicLabels();

                if (rebuildsDetail)
                {
                    EditorApplication.delayCall += () =>
                    {
                        if (panel != null)
                            RebuildProfileDetail();
                    };
                }
            });
            row.Add(field);

            return row;
        }

        /// <summary>
        /// True when <paramref name="property"/> holds a different value than <paramref name="lastValue"/>,
        /// which it then updates.
        /// </summary>
        /// <param name="property">The bound property.</param>
        /// <param name="lastValue">The last value this field acted on.</param>
        /// <remarks>
        /// <b>A change event is not proof of a change.</b> UI Toolkit raises
        /// <see cref="SerializedPropertyChangeEvent"/> when a binding first updates its field — i.e. once per
        /// field every time this pane is built — as well as when a person edits it. Acting on the event
        /// itself made the Target field's rebuild self-sustaining: rebuilding the pane created a new bound
        /// field, whose bind raised the event, which scheduled another rebuild. The visible symptom was the
        /// pane's buttons flickering between hover and not, because the element under the pointer was being
        /// destroyed and recreated continuously.
        /// <para>
        /// It also silenced the harmless-but-real churn from the other fields, each of which rebuilt the
        /// profile rail once on bind for a value nobody had touched.
        /// </para>
        /// </remarks>
        private static bool ValueChanged(SerializedProperty property, ref string lastValue)
        {
            var current = PropertyStamp(property);
            if (current == lastValue)
                return false;

            lastValue = current;
            return true;
        }

        /// <summary>
        /// A comparable string for a serialized property's current value.
        /// </summary>
        /// <param name="property">The property to read; may be null or disposed.</param>
        /// <returns>The value as a string, or null when it cannot be read.</returns>
        /// <remarks>
        /// Covers the property types this section binds. Anything else returns a constant, which means
        /// "never reports a change" — the safe direction, since the cost is a missed refresh rather than a
        /// rebuild loop. A disposed property (the pane was rebuilt under this callback) also lands here.
        /// </remarks>
        private static string PropertyStamp(SerializedProperty property)
        {
            if (property == null)
                return null;

            try
            {
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer:
                    case SerializedPropertyType.Enum:
                        return property.intValue.ToString();
                    case SerializedPropertyType.Boolean:
                        return property.boolValue ? "1" : "0";
                    case SerializedPropertyType.String:
                        return property.stringValue;
                    case SerializedPropertyType.Float:
                        return property.floatValue.ToString("R");
                    case SerializedPropertyType.ObjectReference:
                        return property.objectReferenceInstanceIDValue.ToString();
                    default:
                        return "(unstamped)";
                }
            }
            catch (System.Exception)
            {
                return null;
            }
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

            var lastValue = PropertyStamp(property);
            toggle.RegisterCallback<SerializedPropertyChangeEvent>(_ =>
            {
                // Same guard as BuildProfileField: the bind itself raises this event. See ValueChanged.
                if (!ValueChanged(property, ref lastValue))
                    return;

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
        /// <summary>
        /// Describes what PlayerSettings currently holds, and says so when it disagrees with this asset.
        /// </summary>
        /// <remarks>
        /// The footer used to report the version name alone, which is the half of the mirror that rarely
        /// looked wrong. Reading <c>PlayerSettings version: 0.3.3</c> here while the Player inspector
        /// showed bundle version code <c>2</c> gave no hint that the number a store upload is rejected
        /// over was the stale one — so the divergence is named, with the fix beside it.
        /// </remarks>
        private string DescribePlayerSettingsMirror()
        {
            var target = EditorUserBuildSettings.activeBuildTarget;

            var expectedVersion = _versionSettings.GetBundleVersionString(target);
            var actualVersion = PlayerSettings.bundleVersion;

            // Null for a target with no numeric version code of its own — the desktop targets — where
            // the version name is the whole of the mirror and there is nothing further to compare.
            string expectedCode = null, actualCode = null;
            switch (target)
            {
                case BuildTarget.Android:
                    expectedCode = _versionSettings.GetBuildNumberString();
                    actualCode = PlayerSettings.Android.bundleVersionCode.ToString();
                    break;
                case BuildTarget.iOS:
                    expectedCode = _versionSettings.GetBuildNumberString();
                    actualCode = PlayerSettings.iOS.buildNumber;
                    break;
            }

            var described = actualCode == null
                ? $"PlayerSettings version: {actualVersion}"
                : $"PlayerSettings version: {actualVersion}  ·  version code: {actualCode}";

            var matches = actualVersion == expectedVersion && (actualCode == null || actualCode == expectedCode);
            return matches
                ? described
                : $"{described}   ⚠ differs from this asset — press Sync to Player Settings";
        }

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
                _playerSettingsVersionLabel.text = DescribePlayerSettingsMirror();
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
            MarkBuildPending($"{names.Length} profile(s) · running pre-build checks…");
            EditorApplication.delayCall += () => BuildAllGated(names);
        }

        // async void is the Unity event-handler entry-point exception in the async contract; the body
        // is wrapped so exceptions cannot escape into Unity's synchronization context.
        //
        // Nothing here records the outcome any more: BuildManager records every attempt, including the ones
        // that never run, so the strip and the history report CI and workflow builds too and survive the
        // domain reload a target switch causes.
        private static async void BuildProfileGated(string profileName)
        {
            try
            {
                await BuildManager.BuildAsync(profileName);
            }
            catch (Exception e)
            {
                Debug.LogError($"[BuildManager] Build failed: {e}");
            }
        }

        /// <summary>
        /// Runs the pre-build gate once, then builds each profile.
        /// </summary>
        /// <param name="profileNames">The profiles to build, all targeting the active build target.</param>
        /// <remarks>
        /// The gate is run here explicitly rather than by passing <c>runPreBuildChecks: true</c> for the
        /// first profile only. Both run it once, but the old shape made "was this batch checked?" depend on
        /// loop position — adding a sort, a filter, or a retry ahead of the loop would have silently moved
        /// the gate onto a different profile or dropped it. A batch either passed the gate or did not start.
        /// </remarks>
        private static async void BuildAllGated(string[] profileNames)
        {
            try
            {
                var gate = await MolcaBuildGate.RunAsync();
                if (!gate.Passed)
                {
                    Debug.LogError(gate.DescribeFailure());
                    Debug.LogWarning(
                        $"[BuildManager] Build All did not start: the pre-build gate refused it, so none of " +
                        $"the {profileNames.Length} profile(s) were built.");
                    return;
                }

                for (int i = 0; i < profileNames.Length; i++)
                {
                    var report = await BuildManager.BuildAsync(profileNames[i], runPreBuildChecks: false);
                    if (report == null || report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                    {
                        Debug.LogWarning(
                            $"[BuildManager] Build All stopped at '{profileNames[i]}' " +
                            $"({i + 1} of {profileNames.Length}); the remaining profile(s) were not built.");
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
