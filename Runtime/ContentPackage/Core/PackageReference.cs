using System;
using UnityEngine;

namespace Molca.ContentPackage.Core
{
    /// <summary>
    /// A serializable struct that holds the ID of a content package defined in
    /// <see cref="ContentPackageSettings"/>. Use in MonoBehaviours and ScriptableObjects
    /// as a typed, Inspector-friendly alternative to a raw <c>string</c> package ID.
    /// </summary>
    /// <remarks>
    /// This is a pure data container. Pass <see cref="PackageId"/> to
    /// <see cref="Services.PackageService"/> for all runtime operations.
    /// </remarks>
    [Serializable]
    public struct PackageReference
    {
        [SerializeField] private string _packageId;

#if UNITY_EDITOR
        // Cached at pick-time for display in the Inspector without re-querying settings each frame.
        [SerializeField] private string _cachedDisplayName;
#endif

        /// <summary>The unique package identifier, matching <c>PackageConfig.packageId</c>.</summary>
        public string PackageId => _packageId;

        /// <summary>True when a package ID is assigned.</summary>
        public bool IsValid => !string.IsNullOrEmpty(_packageId);

        /// <summary>Returns the package ID, or <c>"None"</c> if unset.</summary>
        public override string ToString() => IsValid ? _packageId : "None";
    }
}
