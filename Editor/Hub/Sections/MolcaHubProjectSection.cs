using System;
using System.Collections.Generic;
using Molca.Editor.UI.Components;
using Molca.Editor.Icons;
using Molca.Editor.Licensing;
using Molca.Editor.Projects;
using Molca.Editor.Remote;
using Molca;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub.Sections
{
    /// <summary>
    /// Project identity and links section for the Molca Hub Settings workspace.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/Sections/</c>.
    /// Base class: <see cref="VisualElement"/>.
    /// Registration: created by <see cref="MolcaHubWindow"/> when the Project rail section is active.
    /// All editable fields are bound through <see cref="SerializedObject"/>.
    /// </remarks>
    internal sealed class MolcaHubProjectSection : VisualElement
    {
        private readonly SerializedObject _projectSettings;
        private readonly SerializedObject _editorSettings;
        private Label _projectNameLabel;
        private Label _projectIdLabel;
        private Label _projectConnectionLabel;
        private Button _connectButton;
        private Button _createButton;
        private Button _disconnectButton;

        internal MolcaHubProjectSection()
        {
            AddToClassList("molca-hub-project-section");

            _projectSettings = new SerializedObject(MolcaProjectSettings.Instance);
            _editorSettings = new SerializedObject(MolcaEditorSettings.Instance);

            BuildHeader();
            BuildIdentityCard();
            BuildRemoteCard();
            BuildLinksCard();
        }

        private void BuildHeader()
        {
            var header = new VisualElement();
            header.AddToClassList("molca-hub-project-header");
            Add(header);

            var logo = new VisualElement();
            logo.AddToClassList("molca-hub-project-logo");
            AddLogoContent(logo, "molca-hub-project-logo__image", "molca-hub-project-logo__mark");
            header.Add(logo);

            var textStack = new VisualElement();
            textStack.AddToClassList("molca-hub-project-title-stack");
            header.Add(textStack);

            var title = new Label("Molca");
            title.AddToClassList("molca-hub-project-title");
            textStack.Add(title);

            _projectNameLabel = new Label(MolcaProjectSettings.Instance != null ? MolcaProjectSettings.Instance.ProjectName : "Molca Project");
            _projectNameLabel.AddToClassList("molca-hub-project-subtitle");
            textStack.Add(_projectNameLabel);

            var projectNameProperty = _projectSettings.FindProperty("projectName");
            if (projectNameProperty != null)
                textStack.TrackPropertyValue(projectNameProperty, _ => RefreshIdentityLabels());
        }

        private void BuildIdentityCard()
        {
            var card = new MolcaSectionCard("Identity");
            Add(card);

            card.Body.Add(BuildPropertyRow("Company Name", "companyName"));
            card.Body.Add(BuildPropertyRow("Project Name", "projectName"));
            card.Body.Add(BuildProjectIdRow());
            card.Body.Add(BuildLogoRow());
        }

        private void BuildRemoteCard()
        {
            var card = new MolcaSectionCard("Remote Editor");
            Add(card);

            var description = new Label(
                "Connect this Editor to your private Molca dashboard session through an outbound encrypted connection. " +
                "The local MCP token and project contents are never sent.");
            description.AddToClassList("molca-hub-field-description");
            card.Body.Add(description);

            var row = new VisualElement();
            row.AddToClassList("molca-hub-field-row");
            row.Add(BuildFieldLabel("Remote access"));

            var stack = new VisualElement();
            stack.AddToClassList("molca-hub-project-id-stack");
            row.Add(stack);

            var enabled = new Toggle("Enable for my signed-in account");
            enabled.SetValueWithoutNotify(MolcaRemoteSettings.Enabled);
            stack.Add(enabled);

            var assistant = new Toggle("Allow remote Assistant");
            assistant.tooltip =
                "Allows your signed-in dashboard to start and observe the shared in-Editor Assistant. " +
                "Action approvals still require Unity.";
            assistant.SetValueWithoutNotify(MolcaRemoteSettings.AllowAssistant);
            assistant.SetEnabled(MolcaRemoteSettings.Enabled);
            stack.Add(assistant);

            var actions = new Toggle("Allow remote actions");
            actions.tooltip =
                "Allows server-confirmed, locally allowlisted action tools. Remote Assistant uses the " +
                "current Ask, Auto, Plan, or AutoAll mode and never changes it.";
            actions.SetValueWithoutNotify(MolcaRemoteSettings.AllowActions);
            actions.SetEnabled(MolcaRemoteSettings.Enabled);
            stack.Add(actions);

            var status = new Label(MolcaRemoteEditorAgent.Status);
            status.AddToClassList("molca-hub-project-connection-status");
            stack.Add(status);

            enabled.RegisterValueChangedCallback(change =>
            {
                MolcaRemoteSettings.Enabled = change.newValue;
                assistant.SetEnabled(change.newValue);
                actions.SetEnabled(change.newValue);
                MolcaRemoteEditorAgent.ApplySettings();
                status.text = MolcaRemoteEditorAgent.Status;
            });
            assistant.RegisterValueChangedCallback(change =>
            {
                MolcaRemoteSettings.AllowAssistant = change.newValue;
                MolcaRemoteEditorAgent.ApplySettings();
                status.text = MolcaRemoteEditorAgent.Status;
            });
            actions.RegisterValueChangedCallback(change =>
            {
                if (change.newValue && !EditorUtility.DisplayDialog(
                        "Enable Remote Actions",
                        "Remote commands and the shared Assistant may run locally allowlisted action tools. " +
                        "If Assistant action mode is AutoAll, irreversible actions can run without another prompt. " +
                        "Only enable this on an Editor and Molca account you trust.",
                        "Enable", "Cancel"))
                {
                    actions.SetValueWithoutNotify(false);
                    return;
                }
                MolcaRemoteSettings.AllowActions = change.newValue;
                if (!change.newValue)
                    AssistantRemoteFacade.StopForAuthorizationLoss();
                MolcaRemoteEditorAgent.ApplySettings();
                status.text = MolcaRemoteEditorAgent.Status;
            });

            var open = new Button(() => Application.OpenURL(
                DevLicenseConfig.ServerBaseUrl.TrimEnd('/') + "/dashboard"))
            {
                text = "Open Remote dashboard",
                tooltip = "Open the private Remote Editor session list for your signed-in Molca account."
            };
            open.AddToClassList("molca-hub-mini-button");
            stack.Add(open);
            card.Body.Add(row);

            schedule.Execute(() => status.text = MolcaRemoteEditorAgent.Status).Every(1000);
        }

        private VisualElement BuildPropertyRow(string label, string propertyName)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-field-row");

            row.Add(BuildFieldLabel(label));

            var property = _projectSettings.FindProperty(propertyName);
            var field = new PropertyField(property, string.Empty);
            field.AddToClassList("molca-hub-field-control");
            field.BindProperty(property);
            field.RegisterCallback<SerializedPropertyChangeEvent>(_ => RefreshIdentityLabels());
            row.Add(field);

            return row;
        }

        private VisualElement BuildProjectIdRow()
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-field-row");
            row.Add(BuildFieldLabel("Project ID / Code"));

            var stack = new VisualElement();
            stack.AddToClassList("molca-hub-project-id-stack");
            row.Add(stack);

            var box = new VisualElement();
            box.AddToClassList("molca-hub-project-id-box");
            stack.Add(box);

            _projectIdLabel = new Label(ProjectIdText());
            _projectIdLabel.AddToClassList("molca-hub-project-id-text");
            box.Add(_projectIdLabel);

            var copy = new Button(() =>
            {
                EditorGUIUtility.systemCopyBuffer = ProjectIdText();
            })
            {
                text = "Copy",
                tooltip = "Copy project id."
            };
            copy.AddToClassList("molca-hub-mini-button");
            box.Add(copy);

            _projectConnectionLabel = new Label(ProjectConnectionText());
            _projectConnectionLabel.AddToClassList("molca-hub-project-connection-status");
            stack.Add(_projectConnectionLabel);

            _connectButton = new Button(ShowProjectPicker) { text = "Connect" };
            _connectButton.tooltip = "Connect this Unity repository to an existing Molca backend project.";
            _connectButton.AddToClassList("molca-hub-mini-button");
            box.Add(_connectButton);

            _createButton = new Button(CreateAndConnectProject) { text = "Create" };
            _createButton.tooltip = "Create a backend project and connect this Unity repository.";
            _createButton.AddToClassList("molca-hub-mini-button");
            box.Add(_createButton);

            _disconnectButton = new Button(DisconnectProject) { text = "Disconnect" };
            _disconnectButton.tooltip = "Remove the local project binding. This does not delete the backend project.";
            _disconnectButton.AddToClassList("molca-hub-mini-button");
            box.Add(_disconnectButton);

            foreach (string propertyName in new[] { "projectId", "projectCode", "projectBinding" })
            {
                var property = _projectSettings.FindProperty(propertyName);
                if (property != null)
                    row.TrackPropertyValue(property, _ => RefreshIdentityLabels());
            }
            RefreshConnectionControls();

            return row;
        }

        private async void ShowProjectPicker()
        {
            ProjectListResponse projectList = null;
            SetConnectionBusy(true);
            try
            {
                var result = await new MolcaProjectApiClient().ListAsync();
                if (!result.Success)
                {
                    EditorUtility.DisplayDialog("Molca Project", result.Error, "OK");
                    return;
                }
                projectList = result.Value;
            }
            catch (Exception exception) { ShowConnectionException("load projects", exception); }
            finally { SetConnectionBusy(false); }

            if (projectList == null) return;

            var activeProjects = new List<MolcaBackendProject>();
            foreach (var project in projectList.projects ?? Array.Empty<MolcaBackendProject>())
            {
                if (!string.Equals(project.status, "active", StringComparison.OrdinalIgnoreCase)) continue;
                activeProjects.Add(project);
            }
            if (activeProjects.Count == 0)
            {
                EditorUtility.DisplayDialog("Molca Project",
                    "No active projects are available to this account. Create one first.", "OK");
                return;
            }

            // GenericMenu is tied to the IMGUI event that opened it. Since the server request
            // completes asynchronously, a context menu opened here can vanish immediately on
            // Windows. A utility window remains stable after the awaited request.
            MolcaProjectPickerWindow.Open(activeProjects.ToArray(), ConnectProject);
        }

        private async void ConnectProject(MolcaBackendProject project)
        {
            SetConnectionBusy(true);
            try
            {
                var result = await new MolcaProjectApiClient().BindAsync(project.id);
                if (!result.Success)
                {
                    EditorUtility.DisplayDialog("Molca Project", result.Error, "OK");
                    return;
                }
                ApplyProjectBinding(result.Value);
            }
            catch (Exception exception) { ShowConnectionException("connect the project", exception); }
            finally { SetConnectionBusy(false); }
        }

        private async void CreateAndConnectProject()
        {
            string name = MolcaProjectSettings.Instance?.ProjectName?.Trim();
            if (string.IsNullOrEmpty(name)) name = "Molca Project";
            if (!EditorUtility.DisplayDialog("Create Molca Project",
                    $"Create “{name}” in the Molca backend and connect this repository?", "Create", "Cancel"))
                return;

            SetConnectionBusy(true);
            try
            {
                var client = new MolcaProjectApiClient();
                var created = await client.CreateAsync(name);
                if (!created.Success)
                {
                    EditorUtility.DisplayDialog("Molca Project", created.Error, "OK");
                    return;
                }
                var binding = await client.BindAsync(created.Value.id);
                if (!binding.Success)
                {
                    EditorUtility.DisplayDialog("Molca Project",
                        $"The project was created, but could not be connected.\n\n{binding.Error}", "OK");
                    return;
                }
                ApplyProjectBinding(binding.Value);
            }
            catch (Exception exception) { ShowConnectionException("create and connect the project", exception); }
            finally { SetConnectionBusy(false); }
        }

        private void ApplyProjectBinding(ProjectBindingResponse response)
        {
            var settings = MolcaProjectSettings.Instance;
            if (settings == null || response?.project == null) return;
            string entitlement = DevEntitlementStore.LoadEffective();
            DevEntitlementVerifier.Evaluate(entitlement, SystemInfo.deviceUniqueIdentifier, out var user);
            if (!ProjectBindingVerifier.TryVerify(response.projectBinding, response.project.id,
                    response.project.code, user?.licenseeId, out var payload, out var error) ||
                !string.Equals(payload.bindingId, response.bindingId, StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("Molca Project",
                    "The server returned a project connection that could not be verified.\n\n" +
                    (error ?? "Binding identity mismatch."), "OK");
                return;
            }
            Undo.RecordObject(settings, "Connect Molca Project");
            settings.ProjectId = response.project.id;
            settings.ProjectCode = response.project.code;
            settings.ProjectName = response.project.name;
            settings.ProjectBinding = response.projectBinding;
            settings.ProjectBindingVersion = ProjectBindingVerifier.SchemaVersion;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            _projectSettings.Update();
            RefreshIdentityLabels();
        }

        private void DisconnectProject()
        {
            var settings = MolcaProjectSettings.Instance;
            if (!CanManageProjects() || settings == null || string.IsNullOrEmpty(settings.ProjectBinding)) return;
            if (!EditorUtility.DisplayDialog("Disconnect Molca Project",
                    "Remove this repository's project connection? Telemetry will queue and builds will be blocked " +
                    "until it is reconnected. The backend project and its history are unchanged.",
                    "Disconnect", "Cancel"))
                return;
            Undo.RecordObject(settings, "Disconnect Molca Project");
            settings.ProjectId = string.Empty;
            settings.ProjectCode = string.Empty;
            settings.ProjectBinding = string.Empty;
            settings.ProjectBindingVersion = 0;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            _projectSettings.Update();
            RefreshIdentityLabels();
        }

        private void SetConnectionBusy(bool busy)
        {
            bool canManage = CanManageProjects();
            if (_connectButton != null)
                _connectButton.text = busy ? "Loading..." : "Connect";
            _connectButton?.SetEnabled(!busy && canManage);
            _createButton?.SetEnabled(!busy && canManage);
            _disconnectButton?.SetEnabled(!busy && canManage &&
                !string.IsNullOrEmpty(MolcaProjectSettings.Instance?.ProjectBinding));
        }

        private static void ShowConnectionException(string action, Exception exception)
        {
            Debug.LogError($"[Molca Project] Could not {action}: {exception}");
            EditorUtility.DisplayDialog("Molca Project",
                $"Could not {action}.\n\n{exception.Message}", "OK");
        }

        private void RefreshConnectionControls()
        {
            bool canManage = CanManageProjects();
            _connectButton.SetEnabled(canManage);
            _createButton.SetEnabled(canManage);
            _connectButton.tooltip = canManage
                ? "Connect this Unity repository to an existing Molca backend project."
                : "Only Molca owners and managers can assign a backend project.";
            _createButton.tooltip = canManage
                ? "Create a backend project and connect this Unity repository."
                : "Only Molca owners and managers can create a backend project.";
            _disconnectButton.SetEnabled(canManage &&
                !string.IsNullOrEmpty(MolcaProjectSettings.Instance?.ProjectBinding));
        }

        private static bool CanManageProjects()
        {
            string token = DevEntitlementStore.LoadEffective();
            return DevEntitlementVerifier.Evaluate(token, SystemInfo.deviceUniqueIdentifier, out var payload) ==
                   DevLicenseStatus.Valid &&
                   (string.Equals(payload.role, "owner", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(payload.role, "admin", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(payload.role, "manager", StringComparison.OrdinalIgnoreCase));
        }

        private VisualElement BuildLogoRow()
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-field-row");
            row.AddToClassList("molca-hub-field-row--top");
            row.Add(BuildFieldLabel("Project Logo"));

            var logoWrap = new VisualElement();
            logoWrap.AddToClassList("molca-hub-logo-picker");
            row.Add(logoWrap);

            var preview = new VisualElement();
            preview.AddToClassList("molca-hub-logo-preview");
            logoWrap.Add(preview);

            var stack = new VisualElement();
            stack.AddToClassList("molca-hub-logo-picker__stack");
            logoWrap.Add(stack);

            var logoProperty = _projectSettings.FindProperty("projectLogo");
            AddProjectLogoPreview(preview, logoProperty);
            var logoName = new Label(logoProperty.objectReferenceValue != null ? logoProperty.objectReferenceValue.name : "None");
            logoName.AddToClassList("molca-hub-logo-picker__name");
            stack.Add(logoName);

            var field = new PropertyField(logoProperty, string.Empty);
            field.AddToClassList("molca-hub-logo-picker__field");
            field.BindProperty(logoProperty);
            // The preview and name are built from the current value, so refresh them
            // whenever the bound property changes (assigning/clearing the sprite).
            field.RegisterValueChangeCallback(evt =>
            {
                var prop = evt.changedProperty;
                preview.Clear();
                AddProjectLogoPreview(preview, prop);
                logoName.text = prop.objectReferenceValue != null ? prop.objectReferenceValue.name : "None";
            });
            stack.Add(field);

            return row;
        }

        private void BuildLinksCard()
        {
            var card = new MolcaSectionCard("Links");
            Add(card);

            card.Body.Add(BuildLinkRow("Repository", _editorSettings.FindProperty("repositoryUrl")));
            card.Body.Add(BuildDivider());
            card.Body.Add(BuildLinkRow("Documentation", _editorSettings.FindProperty("documentationUrl")));
        }

        private VisualElement BuildLinkRow(string label, SerializedProperty urlProperty)
        {
            var row = new VisualElement();
            row.AddToClassList("molca-hub-link-row");

            row.Add(BuildFieldLabel(label));

            // Click handlers read the property live so they always open the current URL,
            // even after it is edited in Project Settings (same underlying singleton).
            var link = new Button(() => OpenUrl(urlProperty.stringValue));
            link.AddToClassList("molca-hub-link-button");
            row.Add(link);

            var open = new Button(() => OpenUrl(urlProperty.stringValue)) { text = "Open" };
            open.AddToClassList("molca-hub-mini-button");
            row.Add(open);

            void Refresh(string url)
            {
                bool hasUrl = !string.IsNullOrEmpty(url);
                link.text = ShortUrl(url);
                link.tooltip = hasUrl ? url : "No URL configured.";
                link.SetEnabled(hasUrl);
                open.tooltip = hasUrl ? $"Open {url}" : "No URL configured.";
                open.SetEnabled(hasUrl);
            }

            Refresh(urlProperty.stringValue);
            // Live-refresh when the value changes elsewhere (e.g. Project Settings > Molca).
            row.TrackPropertyValue(urlProperty, p => Refresh(p.stringValue));

            return row;
        }

        private static VisualElement BuildDivider()
        {
            var divider = new VisualElement();
            divider.AddToClassList("molca-hub-divider");
            return divider;
        }

        private static Label BuildFieldLabel(string text)
        {
            var label = new Label(text);
            label.AddToClassList("molca-hub-field-label");
            return label;
        }

        private static void AddLogoContent(VisualElement parent, string imageClass, string fallbackClass)
        {
            var icon = MolcaEditorIcons.Logo;
            if (icon != null)
            {
                var image = new Image { image = icon, scaleMode = ScaleMode.ScaleToFit };
                image.AddToClassList(imageClass);
                parent.Add(image);
                return;
            }

            var mark = new Label("m");
            mark.AddToClassList(fallbackClass);
            parent.Add(mark);
        }

        private static void AddProjectLogoPreview(VisualElement parent, SerializedProperty logoProperty)
        {
            if (logoProperty.objectReferenceValue is Sprite sprite && sprite.texture != null)
            {
                var image = new Image { image = sprite.texture, scaleMode = ScaleMode.ScaleToFit };
                image.AddToClassList("molca-hub-logo-preview__image");
                parent.Add(image);
                return;
            }

            AddLogoContent(parent, "molca-hub-logo-preview__image", "molca-hub-logo-preview__mark");
        }

        private static void OpenUrl(string url)
        {
            if (!string.IsNullOrEmpty(url))
                Application.OpenURL(url);
        }

        private string ProjectIdText()
        {
            string code = _projectSettings.FindProperty("projectCode")?.stringValue;
            if (!string.IsNullOrEmpty(code)) return code;
            string id = _projectSettings.FindProperty("projectId")?.stringValue;
            return string.IsNullOrEmpty(id) ? "Not connected" : id;
        }

        private static string ProjectConnectionText()
        {
            var settings = MolcaProjectSettings.Instance;
            if (settings == null || string.IsNullOrWhiteSpace(settings.ProjectBinding))
            {
                string token = DevEntitlementStore.LoadEffective();
                bool signedIn = DevEntitlementVerifier.Evaluate(
                    token, SystemInfo.deviceUniqueIdentifier, out var identity) == DevLicenseStatus.Valid;
                bool canManage = signedIn &&
                    (string.Equals(identity.role, "owner", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(identity.role, "admin", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(identity.role, "manager", StringComparison.OrdinalIgnoreCase));
                return string.IsNullOrWhiteSpace(settings?.ProjectId)
                    ? (canManage || !signedIn
                        ? "Connection required — telemetry is queued and builds are blocked"
                        : "Not connected — ask an owner or manager to assign this project")
                    : "Incomplete connection — reconnect this backend project";
            }

            string entitlement = DevEntitlementStore.LoadEffective();
            DevEntitlementVerifier.Evaluate(entitlement, SystemInfo.deviceUniqueIdentifier, out var user);
            return ProjectBindingVerifier.TryVerify(settings.ProjectBinding, settings.ProjectId, settings.ProjectCode,
                user?.licenseeId, out _, out var error)
                ? "Connected — receipt verified; server validates current access"
                : $"Connection invalid: {error}";
        }

        private void RefreshIdentityLabels()
        {
            _projectSettings.Update();
            if (_projectNameLabel != null)
                _projectNameLabel.text = MolcaProjectSettings.Instance != null
                    ? MolcaProjectSettings.Instance.ProjectName
                    : "Molca Project";

            if (_projectIdLabel != null)
                _projectIdLabel.text = ProjectIdText();
            if (_projectConnectionLabel != null)
                _projectConnectionLabel.text = ProjectConnectionText();
            if (_disconnectButton != null)
                RefreshConnectionControls();
        }

        private static string ShortUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return "Not configured";

            return url
                .Replace("https://", string.Empty)
                .Replace("http://", string.Empty)
                .TrimEnd('/');
        }
    }

    internal sealed class MolcaProjectPickerWindow : EditorWindow
    {
        private MolcaBackendProject[] _projects = Array.Empty<MolcaBackendProject>();
        private Action<MolcaBackendProject> _onSelected;
        private Vector2 _scroll;

        internal static void Open(
            MolcaBackendProject[] projects, Action<MolcaBackendProject> onSelected)
        {
            var window = CreateInstance<MolcaProjectPickerWindow>();
            window.titleContent = new GUIContent("Connect Molca Project");
            window._projects = projects ?? Array.Empty<MolcaBackendProject>();
            window._onSelected = onSelected;
            window.minSize = new Vector2(420f, 180f);
            window.maxSize = new Vector2(640f, 520f);
            window.ShowUtility();
            window.Focus();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Choose a backend project", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "This Unity repository will use the selected project for builds, add-ons, and telemetry.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(8f);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var project in _projects)
            {
                if (project == null) continue;
                if (!GUILayout.Button($"{project.name}  ({project.code})", GUILayout.Height(30f))) continue;

                var onSelected = _onSelected;
                Close();
                onSelected?.Invoke(project);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6f);
            if (GUILayout.Button("Cancel"))
                Close();
        }
    }
}
