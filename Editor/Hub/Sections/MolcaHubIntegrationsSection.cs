using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Molca.Settings.Integration;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub.Sections
{
    /// <summary>
    /// Integrations section for the Molca Hub Settings workspace.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/Sections/</c>.
    /// Base class: <see cref="VisualElement"/>.
    /// Registration: created by <see cref="MolcaHubWindow"/> when the Integrations rail section is active.
    /// <para>
    /// The section is <b>data-driven</b> over <see cref="IntegrationSettings"/>: one card is rendered per
    /// registered <see cref="IntegrationProvider"/>, reading live <see cref="IntegrationProvider.IsConnected"/>
    /// and <see cref="IntegrationProvider.StatusMessage"/>, and each card is a <i>launcher</i> that pings the
    /// provider asset so its inspector (the real config surface) opens — no secrets or network calls live on
    /// the card itself. Discord additionally keeps a launcher to the Editor section because it can also be a
    /// notification provider (not only an integration provider). <see cref="BuildAddRow"/> discovers
    /// <see cref="IntegrationProvider"/> subtypes that are not yet registered (Sprint 29) so
    /// <c>+ Add integration</c> creates a real provider asset; it disables when all known types are added.
    /// </para>
    /// </remarks>
    internal sealed class MolcaHubIntegrationsSection : VisualElement
    {
        // Provider sub-assets live alongside the settings singletons, under the shared canonical folder.
        private const string IntegrationsAssetDir = MolcaEditorSettingsAsset.CanonicalFolder + "/Integrations";

        private readonly Action<MolcaHubSection> _navigate;

        // Live status bindings for the rendered provider cards, refreshed on a timer (below).
        private readonly List<(IntegrationProvider provider, VisualElement dot, Label label)> _statusBindings = new();

        internal MolcaHubIntegrationsSection(Action<MolcaHubSection> navigate)
        {
            _navigate = navigate;
            AddToClassList("molca-hub-integrations-section");
            Rebuild();

            // A provider is connected/configured from its own inspector (a separate window), so the section
            // gets no callback. Poll to keep each card's status dot + label live without a full rebuild.
            schedule.Execute(RefreshStatuses).Every(500);
        }

        /// <summary>Clears and re-renders the section (header, provider cards, add row).</summary>
        private void Rebuild()
        {
            Clear();
            _statusBindings.Clear();

            BuildHeader();

            // Discord is also a notification provider: keep a launcher to its notification config in the Editor section.
            Add(BuildProviderCard(
                "D", "rgb(88, 101, 242)", "Discord (Notifications)", "Build & error notifications",
                connected: true,
                statusText: "Via Notification Providers",
                actionText: "Configure",
                action: () => _navigate?.Invoke(MolcaHubSection.Editor),
                actionTooltip: "Discord build notifications are configured as a notification provider in the Editor section."));

            BuildProviderCards();

            Add(BuildAddRow());
        }

        /// <summary>Renders one launcher card per registered integration provider.</summary>
        private void BuildProviderCards()
        {
            var settings = IntegrationSettings.FindSettings();
            if (settings == null) return;

            foreach (var provider in settings.Providers)
            {
                var captured = provider;
                var card = BuildProviderCard(
                    provider.Glyph, provider.GlyphColor, provider.DisplayName, provider.Description,
                    connected: IsConfigured(provider),
                    statusText: provider.StatusMessage,
                    actionText: "Configure",
                    action: () =>
                    {
                        Selection.activeObject = captured;
                        EditorGUIUtility.PingObject(captured);
                    },
                    actionTooltip: $"Open the {provider.DisplayName} integration settings to connect or configure it.");
                Add(card);

                // Bind the card's status elements so the timer can refresh them in place.
                var dot = card.Q<VisualElement>(className: "molca-hub-status-dot");
                var label = card.Q<Label>(className: "molca-hub-integration-status__label");
                if (dot != null && label != null)
                    _statusBindings.Add((captured, dot, label));
            }
        }

        // A provider reads as "configured" when it has a stored credential (persists across domain reloads)
        // or is verified-connected this session. IsConnected alone resets on every recompile.
        // OAuth providers store tokens in OAuthCredentialStore, which the Core PAT-only HasCredential does
        // not see — so include the OAuth bundle for OAuth-capable providers (Sprint 32).
        private static bool IsConfigured(IntegrationProvider provider)
            => provider.IsConnected
               || provider.HasCredential
               || (provider is Molca.Settings.Integration.OAuth.OAuthIntegrationProvider oauth && oauth.HasOAuthTokens);

        // Re-reads live provider state into the existing cards. Cheap; runs on the schedule timer.
        private void RefreshStatuses()
        {
            foreach (var (provider, dot, label) in _statusBindings)
            {
                if (provider == null) continue;
                bool configured = IsConfigured(provider);
                dot.EnableInClassList("molca-hub-status-dot--ok", configured);
                dot.EnableInClassList("molca-hub-status-dot--idle", !configured);
                label.text = provider.StatusMessage;
            }
        }

        private void BuildHeader()
        {
            var title = new Label("Integrations");
            title.AddToClassList("molca-hub-integrations-title");
            Add(title);

            var subtitle = new Label("Connect external services for notifications, issue sync, and CI. Each card opens the provider's settings.");
            subtitle.AddToClassList("molca-hub-integrations-subtitle");
            Add(subtitle);
        }

        private VisualElement BuildProviderCard(
            string glyph, string glyphColor, string name, string description,
            bool connected, string statusText, string actionText, Action action, string actionTooltip)
        {
            var card = new VisualElement();
            card.AddToClassList("molca-hub-integration-card");

            var icon = new Label(glyph);
            icon.AddToClassList("molca-hub-integration-icon");
            icon.style.backgroundColor = ParseColor(glyphColor);
            card.Add(icon);

            var stack = new VisualElement();
            stack.AddToClassList("molca-hub-integration-stack");
            card.Add(stack);

            var nameLabel = new Label(name);
            nameLabel.AddToClassList("molca-hub-integration-name");
            stack.Add(nameLabel);

            var descLabel = new Label(description);
            descLabel.AddToClassList("molca-hub-integration-desc");
            stack.Add(descLabel);

            var spacer = new VisualElement();
            spacer.AddToClassList("molca-hub-spacer");
            card.Add(spacer);

            var statusWrap = new VisualElement();
            statusWrap.AddToClassList("molca-hub-integration-status");
            card.Add(statusWrap);

            var dot = new VisualElement();
            dot.AddToClassList("molca-hub-status-dot");
            dot.AddToClassList(connected ? "molca-hub-status-dot--ok" : "molca-hub-status-dot--idle");
            statusWrap.Add(dot);

            var statusLabel = new Label(statusText);
            statusLabel.AddToClassList("molca-hub-integration-status__label");
            statusWrap.Add(statusLabel);

            var actionButton = new Button(() => action?.Invoke()) { text = actionText, tooltip = actionTooltip };
            actionButton.AddToClassList("molca-hub-mini-button");
            actionButton.SetEnabled(action != null);
            card.Add(actionButton);

            return card;
        }

        private VisualElement BuildAddRow()
        {
            var available = AvailableProviderTypes();

            var button = new Button { text = "+ Add integration" };
            button.AddToClassList("molca-hub-integration-add");

            if (available.Count == 0)
            {
                // Either no provider types exist, or every known type is already registered.
                button.SetEnabled(false);
                button.tooltip = "All available integrations have been added.";
                return button;
            }

            button.tooltip = "Create and register an integration provider.";
            button.clicked += () => ShowAddMenu(available);
            return button;
        }

        // Concrete IntegrationProvider subtypes not yet present in the registry.
        private static List<Type> AvailableProviderTypes()
        {
            var settings = IntegrationSettings.FindSettings();
            var registered = settings == null
                ? new HashSet<Type>()
                : new HashSet<Type>(settings.Providers.Select(p => p.GetType()));

            return TypeCache.GetTypesDerivedFrom<IntegrationProvider>()
                .Where(t => !t.IsAbstract && !registered.Contains(t))
                .OrderBy(t => t.Name)
                .ToList();
        }

        private void ShowAddMenu(List<Type> available)
        {
            var menu = new GenericMenu();
            foreach (var type in available)
            {
                var captured = type;
                menu.AddItem(new GUIContent(DisplayNameFor(type)), false, () => CreateAndRegister(captured));
            }
            menu.ShowAsContext();
        }

        // Creates the provider asset, registers it on the (created-if-needed) settings asset, and re-renders.
        private void CreateAndRegister(Type type)
        {
            var settings = IntegrationSettings.GetOrCreateSettings();

            if (!Directory.Exists(IntegrationsAssetDir))
                Directory.CreateDirectory(IntegrationsAssetDir);

            var asset = (IntegrationProvider)ScriptableObject.CreateInstance(type);
            asset.name = type.Name;
            var assetPath = AssetDatabase.GenerateUniqueAssetPath($"{IntegrationsAssetDir}/{type.Name}.asset");
            AssetDatabase.CreateAsset(asset, assetPath);

            // Append through SerializedObject so undo/dirtying/persistence behave like an inspector edit.
            var so = new SerializedObject(settings);
            var list = so.FindProperty("providers");
            int index = list.arraySize;
            list.InsertArrayElementAtIndex(index);
            list.GetArrayElementAtIndex(index).objectReferenceValue = asset;
            so.ApplyModifiedProperties();

            AssetDatabase.SaveAssets();

            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
            Rebuild();
        }

        // Reads the provider's authored DisplayName from a throwaway instance so the menu shows "GitHub",
        // not "GitHubIntegrationProvider". The temp instance is discarded immediately.
        private static string DisplayNameFor(Type type)
        {
            IntegrationProvider temp = null;
            try
            {
                temp = (IntegrationProvider)ScriptableObject.CreateInstance(type);
                return string.IsNullOrEmpty(temp.DisplayName) ? type.Name : temp.DisplayName;
            }
            catch
            {
                return type.Name;
            }
            finally
            {
                if (temp != null) UnityEngine.Object.DestroyImmediate(temp);
            }
        }

        private static Color ParseColor(string rgb)
        {
            // rgb strings are authored as "rgb(r, g, b)" to keep the card glyph colors local.
            var inner = rgb.Substring(rgb.IndexOf('(') + 1).TrimEnd(')');
            var parts = inner.Split(',');
            return new Color( // doctor:ignore — parses an authored rgb() glyph color, not chrome
                int.Parse(parts[0].Trim()) / 255f,
                int.Parse(parts[1].Trim()) / 255f,
                int.Parse(parts[2].Trim()) / 255f);
        }
    }
}

