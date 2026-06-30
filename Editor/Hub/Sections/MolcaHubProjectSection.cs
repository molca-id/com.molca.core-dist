using Molca.Editor.UI.Components;
using Molca.Editor.Icons;
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

        internal MolcaHubProjectSection()
        {
            AddToClassList("molca-hub-project-section");

            _projectSettings = new SerializedObject(MolcaProjectSettings.Instance);
            _editorSettings = new SerializedObject(MolcaEditorSettings.Instance);

            BuildHeader();
            BuildIdentityCard();
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

            var box = new VisualElement();
            box.AddToClassList("molca-hub-project-id-box");
            row.Add(box);

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

            var projectIdProperty = _projectSettings.FindProperty("projectId");
            if (projectIdProperty != null)
                row.TrackPropertyValue(projectIdProperty, _ => RefreshIdentityLabels());

            return row;
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
            var id = _projectSettings.FindProperty("projectId").stringValue;
            return string.IsNullOrEmpty(id) ? "MOLCA-0001" : id;
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
}
