using Molca.Settings;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Settings
{
    /// <summary>
    /// Editor-time validator for bootstrap-critical assets (<see cref="MolcaProjectSettings"/>
    /// and <see cref="GlobalSettings"/>).
    /// </summary>
    /// <remarks>
    /// Runs once per domain reload. Surfaces misconfigurations as <see cref="Debug.LogError"/>
    /// entries in the console immediately, rather than as a <see cref="System.NullReferenceException"/>
    /// during play-mode bootstrap.
    /// <para>
    /// The rules live in <see cref="BootstrapCheck"/>, which this only logs — see that type for the list
    /// of validations and their finding codes. Keeping one copy of the rules is what stops a repair and a
    /// console warning from disagreeing about what is wrong.
    /// </para>
    /// </remarks>
    public static class BootstrapAssetValidator
    {
        private const string LogPrefix = "[BootstrapAssetValidator]";

        [InitializeOnLoadMethod]
        private static void RegisterValidator()
        {
            // Defer until after the AssetDatabase is fully ready post-reload.
            EditorApplication.delayCall += Validate;
        }

        /// <summary>
        /// Logs whatever <see cref="BootstrapCheck"/> finds.
        /// </summary>
        /// <remarks>
        /// The rules themselves moved to <see cref="BootstrapCheck"/> so remediation keys fixes to the same
        /// coded findings this logs — a second copy of the rules here is exactly how a "fix" and a warning
        /// end up disagreeing about what is wrong. The console text is unchanged in substance; each line now
        /// carries its finding code so a reader can look the repair up.
        /// </remarks>
        private static void Validate()
        {
            foreach (var finding in BootstrapCheck.Run())
                Debug.LogError($"{LogPrefix} [{finding.Code}] {finding.Message}", finding.Context);
        }
    }
}
