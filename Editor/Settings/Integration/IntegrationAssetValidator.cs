using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Molca.Settings.Integration
{
    /// <summary>
    /// Validates the integration provider configuration on every domain reload, in parity with
    /// <c>McpAssetValidator</c> / <c>BootstrapAssetValidator</c>.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/</c>.
    /// Registration: <see cref="InitializeOnLoadMethodAttribute"/> hook; not an asset.
    /// Reports null entries and duplicate provider types as errors (a misconfigured registry), and an enabled
    /// provider that has no stored credential as a warning (a common, recoverable setup state). Integrations
    /// are optional, so the absence of the settings asset is not an error.
    /// </remarks>
    public static class IntegrationAssetValidator
    {
        [InitializeOnLoadMethod]
        private static void RegisterValidator()
        {
            // Defer until the AssetDatabase is fully ready after the reload.
            EditorApplication.delayCall += Validate;
        }

        private static void Validate()
        {
            var settings = IntegrationSettings.FindSettings();
            if (settings == null)
                return; // Integrations are optional; absence is not an error.

            var so = new SerializedObject(settings);
            var list = so.FindProperty("providers");
            if (list == null)
                return;

            var seenTypes = new HashSet<Type>();
            for (var i = 0; i < list.arraySize; i++)
            {
                var provider = list.GetArrayElementAtIndex(i).objectReferenceValue as IntegrationProvider;
                if (provider == null)
                {
                    Debug.LogError($"[Molca Integration] Provider list entry {i} is null in '{settings.name}'.");
                    continue;
                }

                var type = provider.GetType();
                if (!seenTypes.Add(type))
                    Debug.LogError($"[Molca Integration] Duplicate provider type '{type.Name}' in '{settings.name}'.");

                if (provider.Enabled && !provider.HasCredential)
                    Debug.LogWarning(
                        $"[Molca Integration] '{provider.DisplayName}' is enabled but has no stored credential. " +
                        "Open its settings to connect, or disable it.");
            }
        }
    }
}

