using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Molca.Settings.Integration
{
    /// <summary>
    /// Registry asset that owns every <see cref="IntegrationProvider"/> in the project.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/</c>.
    /// Base class: <see cref="ScriptableObject"/>.
    /// Registration: a single asset, located (or created) via <see cref="GetOrCreateSettings"/>; the Molca
    /// Hub Integrations section iterates <see cref="Providers"/> to render one card per provider. Mirrors
    /// <c>NotificationSettings</c>' role for notification providers.
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-settings.png")]
    [CreateAssetMenu(fileName = "Integration Settings", menuName = "Molca/Editor/Integration Settings", order = 110)]
    public class IntegrationSettings : ScriptableObject
    {
        [SerializeField] private List<IntegrationProvider> providers = new List<IntegrationProvider>();

        /// <summary>All registered providers, skipping null entries.</summary>
        public IEnumerable<IntegrationProvider> Providers
        {
            get
            {
                if (providers == null) yield break;
                foreach (var provider in providers)
                {
                    if (provider != null) yield return provider;
                }
            }
        }

        /// <summary>Returns the registered provider of type <typeparamref name="T"/>, or <c>null</c>.</summary>
        public T GetProvider<T>() where T : IntegrationProvider
        {
            if (providers == null) return null;
            foreach (var provider in providers)
            {
                if (provider is T typed) return typed;
            }
            return null;
        }

        /// <summary>Returns the registered provider matching <paramref name="type"/>, or <c>null</c>.</summary>
        public IntegrationProvider GetProvider(Type type)
        {
            if (providers == null || type == null) return null;
            foreach (var provider in providers)
            {
                if (provider != null && type.IsInstanceOfType(provider)) return provider;
            }
            return null;
        }

        /// <summary>
        /// Loads the project's <see cref="IntegrationSettings"/> asset without creating one.
        /// </summary>
        /// <returns>The existing settings asset, or <c>null</c> if none exists yet.</returns>
        /// <remarks>Use this on read/render paths (e.g. the Hub) so merely viewing never creates an asset.</remarks>
        public static IntegrationSettings FindSettings()
        {
            var found = AssetDatabase.FindAssets("t:IntegrationSettings");
            if (found.Length == 0) return null;
            var path = AssetDatabase.GUIDToAssetPath(found[0]);
            return AssetDatabase.LoadAssetAtPath<IntegrationSettings>(path);
        }

        /// <summary>
        /// Loads the project's <see cref="IntegrationSettings"/> asset, creating one on first use.
        /// </summary>
        /// <remarks>
        /// Editor-only. Created in <see cref="Molca.Editor.MolcaEditorSettingsAsset.CanonicalFolder"/> via the
        /// shared <see cref="Molca.Editor.MolcaEditorSettingsAsset.GetOrCreate{T}"/> helper — the single
        /// default location for all Core editor settings assets.
        /// </remarks>
        public static IntegrationSettings GetOrCreateSettings()
            => Molca.Editor.MolcaEditorSettingsAsset.GetOrCreate<IntegrationSettings>("Integration Settings.asset");
    }
}
