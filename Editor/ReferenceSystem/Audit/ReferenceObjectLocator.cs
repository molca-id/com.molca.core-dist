using System;
using UnityEditor;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>
    /// A durable editor-side address for the object that owns a reference site or provides a
    /// reference target: asset, object, and human-readable path.
    /// </summary>
    /// <remarks>
    /// Locators exist so a finding can be navigated back to and so a repair can state its
    /// preconditions, without the audit holding a live <see cref="UnityEngine.Object"/>. Identity is
    /// <see cref="AssetGuid"/> plus <see cref="LocalFileId"/> — stable across renames, reimports, and
    /// domain reloads, unlike an object name or an instance id. This type is editor-only and must
    /// never enter a player build.
    /// </remarks>
    public readonly struct ReferenceObjectLocator : IEquatable<ReferenceObjectLocator>
    {
        /// <summary>GUID of the asset containing the object (scene, prefab, or ScriptableObject asset).</summary>
        public string AssetGuid { get; }

        /// <summary>Local file id of the object within its asset.</summary>
        public long LocalFileId { get; }

        /// <summary>Asset path, for display only — <see cref="AssetGuid"/> is the identity.</summary>
        public string AssetPath { get; }

        /// <summary>Hierarchy path of the owning GameObject, or the asset name for a ScriptableObject.</summary>
        public string ObjectPath { get; }

        /// <summary>Name of the concrete owning type (component or ScriptableObject class).</summary>
        public string TypeName { get; }

        /// <summary>
        /// Serialized <see cref="GlobalObjectId"/> when Unity could produce one; used by
        /// <see cref="TryResolve"/> to navigate back to the object.
        /// </summary>
        public string GlobalId { get; }

        /// <summary>
        /// Session-scoped instance id, used only as a fallback by <see cref="TryResolve"/>.
        /// </summary>
        /// <remarks>
        /// A <see cref="GlobalObjectId"/> does not round-trip for an object in an unsaved or untitled
        /// scene — there is no asset GUID to anchor it to — so a locator built from one cannot be resolved
        /// back through it. That case is not exotic: it is every object a user has just created and not yet
        /// saved. The instance id covers it within the current editor session, and is deliberately never
        /// treated as identity: it is not part of <see cref="Key"/> or <see cref="Equals(ReferenceObjectLocator)"/>,
        /// because it changes on domain reload.
        /// </remarks>
        internal int InstanceIdHint { get; }

        /// <summary>
        /// Stable string form of this locator's identity. Suitable as a dictionary key.
        /// </summary>
        /// <remarks>
        /// Falls back to the type, object path, and session instance id when the object has neither a GUID
        /// nor a local file id — an object in an unsaved scene, or a <see cref="Synthetic"/> locator. Such an
        /// object has no durable identity to offer, so a session-unique key is the best available answer, and
        /// strictly better than two same-named objects sharing one.
        /// </remarks>
        public string Key => string.IsNullOrEmpty(AssetGuid) && LocalFileId == 0
            ? $"synthetic:{TypeName}:{ObjectPath}:{InstanceIdHint}"
            : $"{AssetGuid}:{LocalFileId}";

        /// <summary>True when the locator carries enough identity to be compared or resolved.</summary>
        public bool IsValid => !string.IsNullOrEmpty(AssetGuid) || !string.IsNullOrEmpty(GlobalId);

        internal ReferenceObjectLocator(
            string assetGuid, long localFileId, string assetPath, string objectPath, string typeName,
            string globalId, int instanceIdHint = 0)
        {
            AssetGuid = assetGuid ?? string.Empty;
            LocalFileId = localFileId;
            AssetPath = assetPath ?? string.Empty;
            ObjectPath = objectPath ?? string.Empty;
            TypeName = typeName ?? string.Empty;
            GlobalId = globalId ?? string.Empty;
            InstanceIdHint = instanceIdHint;
        }

        /// <summary>
        /// Builds a locator for <paramref name="target"/>.
        /// </summary>
        /// <param name="target">The component or asset to address. May be null.</param>
        /// <param name="assetPathHint">
        /// Asset path to record when Unity cannot report one (an object inside an unsaved scene, or a
        /// prefab loaded by path). Ignored when the object already has a discoverable path.
        /// </param>
        /// <returns>A locator; <see cref="IsValid"/> is false when no identity could be established.</returns>
        public static ReferenceObjectLocator For(UnityEngine.Object target, string assetPathHint = null)
        {
            if (target == null)
                return default;

            // GetGlobalObjectIdSlow is the only API that addresses both assets and scene objects
            // uniformly. It is "slow" per call, so callers batch one locator per discovered object
            // rather than recomputing during a redraw.
            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(target);
            var assetGuid = globalId.assetGUID.ToString();
            if (assetGuid == "00000000000000000000000000000000")
                assetGuid = string.Empty;

            var assetPath = string.IsNullOrEmpty(assetGuid)
                ? assetPathHint
                : AssetDatabase.GUIDToAssetPath(assetGuid);
            if (string.IsNullOrEmpty(assetPath))
                assetPath = assetPathHint;

            return new ReferenceObjectLocator(
                assetGuid,
                unchecked((long)globalId.targetObjectId),
                assetPath,
                DescribeObjectPath(target),
                target.GetType().Name,
                globalId.ToString(),
                target.GetInstanceID());
        }

        /// <summary>
        /// Builds a locator for something Unity cannot address directly.
        /// </summary>
        /// <param name="assetPath">Project-relative path of the owning asset. Used to derive the GUID.</param>
        /// <param name="objectPath">Human-readable path of the object within that asset.</param>
        /// <param name="typeName">Name of the owning type.</param>
        /// <param name="localFileId">Local file id, when the caller knows one.</param>
        /// <returns>A locator that can be compared and displayed but not navigated to.</returns>
        /// <remarks>
        /// For <see cref="MolcaReferenceIndexContributor"/> implementations describing records that have no
        /// single backing <see cref="UnityEngine.Object"/>, and for tests. <see cref="TryResolve"/> returns
        /// null for these — there is no <see cref="GlobalObjectId"/> to resolve.
        /// </remarks>
        public static ReferenceObjectLocator Synthetic(
            string assetPath, string objectPath, string typeName, long localFileId = 0)
        {
            var guid = string.IsNullOrEmpty(assetPath) ? string.Empty : AssetDatabase.AssetPathToGUID(assetPath);
            return new ReferenceObjectLocator(guid, localFileId, assetPath, objectPath, typeName, globalId: null);
        }

        /// <summary>
        /// Resolves this locator back to a live object, loading its asset if necessary.
        /// </summary>
        /// <returns>The object, or null when it no longer exists or its scene is not open.</returns>
        public UnityEngine.Object TryResolve()
        {
            if (!string.IsNullOrEmpty(GlobalId) && GlobalObjectId.TryParse(GlobalId, out var parsed))
            {
                // Returns null (rather than throwing) for an object whose scene is closed or whose
                // asset was deleted — navigation callers surface that as "open the scene first".
                var resolved = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsed);
                if (resolved != null)
                    return resolved;
            }

            // Fallback for an object in an unsaved scene, whose GlobalObjectId has no asset GUID to anchor
            // it to and therefore cannot round-trip. Valid only within this editor session, which is
            // exactly the lifetime of the snapshot that produced this locator.
            return InstanceIdHint == 0 ? null : EditorUtility.EntityIdToObject(InstanceIdHint);
        }

        /// <summary>Hierarchy path for a component, or the object name for an asset.</summary>
        private static string DescribeObjectPath(UnityEngine.Object target)
        {
            if (target is UnityEngine.Component component && component.gameObject != null)
            {
                var path = component.gameObject.name;
                for (var parent = component.transform.parent; parent != null; parent = parent.parent)
                    path = parent.name + "/" + path;
                return path;
            }

            return target.name;
        }

        /// <inheritdoc/>
        public bool Equals(ReferenceObjectLocator other) =>
            LocalFileId == other.LocalFileId &&
            string.Equals(AssetGuid, other.AssetGuid, StringComparison.Ordinal) &&
            string.Equals(GlobalId, other.GlobalId, StringComparison.Ordinal);

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is ReferenceObjectLocator other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(AssetGuid, LocalFileId, GlobalId);

        /// <summary>
        /// Best available human-readable description.
        /// </summary>
        /// <remarks>
        /// Degrades through whatever it has rather than keying off <see cref="IsValid"/>. A locator can lack a
        /// resolvable identity — an object in an unsaved scene, or a contributed record — while still knowing
        /// perfectly well which asset and object it means, and a repair preview that printed
        /// <c>&lt;unknown&gt;</c> for those would be describing a change the user cannot place.
        /// </remarks>
        public override string ToString()
        {
            var asset = !string.IsNullOrEmpty(AssetPath) ? AssetPath
                : !string.IsNullOrEmpty(AssetGuid) ? AssetGuid
                : null;

            if (asset != null)
                return string.IsNullOrEmpty(ObjectPath) ? asset : $"{asset} :: {ObjectPath}";

            if (!string.IsNullOrEmpty(ObjectPath))
                return string.IsNullOrEmpty(TypeName) ? ObjectPath : $"{ObjectPath} ({TypeName})";

            return string.IsNullOrEmpty(TypeName) ? "<unknown>" : $"<unsaved {TypeName}>";
        }
    }
}
