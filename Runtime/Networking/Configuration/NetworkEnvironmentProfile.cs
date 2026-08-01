using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.Networking.Configuration
{
    /// <summary>How a deployment context should be treated by safety rules.</summary>
    public enum NetworkEnvironmentClassification
    {
        /// <summary>A developer machine. Plain HTTP and loopback hosts are expected.</summary>
        Local = 0,

        /// <summary>A shared development deployment.</summary>
        Development,

        /// <summary>An automated-test or QA deployment.</summary>
        Test,

        /// <summary>A pre-production deployment mirroring production topology.</summary>
        Staging,

        /// <summary>A live deployment serving real users. Subject to production safety rules.</summary>
        Production,

        /// <summary>A context that does not fit the others; safety rules are authored explicitly.</summary>
        Custom
    }

    /// <summary>
    /// Non-secret deployment identity for one environment — one half of a
    /// <see cref="NetworkRouteKey"/>. Serialized inside <see cref="NetworkCatalog"/>.
    /// </summary>
    /// <remarks>
    /// Holds no origins of its own: origins live on each service's
    /// <see cref="NetworkServiceBinding"/> for this environment. An environment is identity plus
    /// safety posture, not an address.
    /// </remarks>
    [Serializable]
    public class NetworkEnvironmentProfile
    {
        [SerializeField] private string _id = "";
        [SerializeField] private string _displayName = "";
        [SerializeField] private NetworkEnvironmentClassification _classification = NetworkEnvironmentClassification.Development;

        [Tooltip("Build target names (UnityEditor.BuildTarget) this environment may be selected for. Empty means any target.")]
        [SerializeField] private List<string> _enabledBuildTargets = new List<string>();

        [Tooltip("Build profile asset names this environment maps to. Empty means no profile binding.")]
        [SerializeField] private List<string> _enabledBuildProfiles = new List<string>();

        [Tooltip("Policy profile ID applied to every service in this environment, unless the service or endpoint overrides it. Empty inherits the catalog default.")]
        [SerializeField] private string _policyProfileId = "";

        [Tooltip("Require https/wss for every origin bound to this environment. Forced on for Production.")]
        [SerializeField] private bool _requireSecureTransport = false;

        [Tooltip("Non-secret labels surfaced in diagnostics, e.g. region or tenant.")]
        [SerializeField] private List<string> _labels = new List<string>();

        [Tooltip("Author notes shown in the Hub. Never include credentials.")]
        [SerializeField, TextArea(1, 4)] private string _notes = "";

        /// <summary>Stable kebab-case identifier. See <see cref="NetworkIds"/>.</summary>
        public string Id => _id;

        /// <summary>Human-readable name. Safe to rename at any time.</summary>
        public string DisplayName => string.IsNullOrEmpty(_displayName) ? _id : _displayName;

        /// <summary>How safety rules treat this environment.</summary>
        public NetworkEnvironmentClassification Classification => _classification;

        /// <summary>
        /// <c>UnityEditor.BuildTarget</c> names this environment may be selected for.
        /// Empty means every target.
        /// </summary>
        public IReadOnlyList<string> EnabledBuildTargets => _enabledBuildTargets;

        /// <summary>Build profile asset names this environment maps to. Empty means no profile binding.</summary>
        public IReadOnlyList<string> EnabledBuildProfiles => _enabledBuildProfiles;

        /// <summary>
        /// Environment-level policy override ID, or empty to inherit
        /// <see cref="NetworkCatalog.DefaultPolicyProfileId"/>.
        /// </summary>
        public string PolicyProfileId => _policyProfileId;

        /// <summary>
        /// Whether every origin bound to this environment must use an encrypted scheme.
        /// Always <c>true</c> for <see cref="NetworkEnvironmentClassification.Production"/>,
        /// regardless of the authored value.
        /// </summary>
        public bool RequireSecureTransport =>
            _requireSecureTransport || _classification == NetworkEnvironmentClassification.Production;

        /// <summary>
        /// Whether production safety rules apply — mutation confirmation in the request console,
        /// secure schemes, and no TLS relaxation.
        /// </summary>
        public bool IsProductionSafetyEnforced =>
            _classification == NetworkEnvironmentClassification.Production;

        /// <summary>Non-secret diagnostic labels.</summary>
        public IReadOnlyList<string> Labels => _labels;

        /// <summary>Author notes. Non-secret by contract.</summary>
        public string Notes => _notes;

        /// <summary>
        /// Whether this environment is selectable for the named build target.
        /// </summary>
        /// <param name="buildTargetName">A <c>UnityEditor.BuildTarget</c> name, e.g. <c>StandaloneWindows64</c>.</param>
        /// <returns>
        /// <c>true</c> when <see cref="EnabledBuildTargets"/> is empty (any target) or contains
        /// <paramref name="buildTargetName"/>.
        /// </returns>
        public bool SupportsBuildTarget(string buildTargetName)
        {
            if (_enabledBuildTargets == null || _enabledBuildTargets.Count == 0)
                return true;

            for (int i = 0; i < _enabledBuildTargets.Count; i++)
            {
                if (string.Equals(_enabledBuildTargets[i], buildTargetName, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Creates a profile in code. Used by migration, import, and tests; authored profiles come
        /// from Unity deserialization instead.
        /// </summary>
        /// <param name="id">Stable identifier; must satisfy <see cref="NetworkIds.IsValid"/>.</param>
        /// <param name="displayName">Human-readable name, or <c>null</c> to reuse <paramref name="id"/>.</param>
        /// <param name="classification">Safety posture for this environment.</param>
        internal static NetworkEnvironmentProfile Create(
            string id,
            string displayName,
            NetworkEnvironmentClassification classification)
        {
            return new NetworkEnvironmentProfile
            {
                _id = id,
                _displayName = displayName ?? id,
                _classification = classification
            };
        }
    }
}
