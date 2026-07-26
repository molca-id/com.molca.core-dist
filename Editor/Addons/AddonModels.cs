using System;

namespace Molca.Editor.Addons
{
    /// <summary>Top-level response returned by the licensed add-on catalog endpoint.</summary>
    [Serializable]
    internal sealed class AddonCatalogResponse
    {
        public int schemaVersion;
        public string licenseeId;

        /// <summary>Channel actually served, after the licensee's ceiling capped the request.</summary>
        public string channel;

        /// <summary>Highest channel this license may request; drives which options the UI offers.</summary>
        public string maxChannel;
        public string projectId;
        public string role;
        public bool canManagePolicy;

        public AddonCatalogPackage[] packs;
    }

    /// <summary>
    /// One add-on family and the compatible versions visible to the current licensee, newest first.
    /// Every valid framework license sees every active pack — there is no per-pack entitlement.
    /// </summary>
    [Serializable]
    internal sealed class AddonCatalogPackage
    {
        public string id;
        public string name;
        public string description;
        public string publisher;
        public string policyStatus;
        public bool requested;
        public AddonCatalogVersion[] versions;

        /// <summary>The newest compatible version, or null when the server offered none.</summary>
        public AddonCatalogVersion Latest => versions != null && versions.Length > 0 ? versions[0] : null;
    }

    /// <summary>
    /// Compatibility and acquisition metadata for one immutable add-on version. <c>hasRuntime</c> lives
    /// here rather than on the pack because it is derived from each archive at publish time: a pack can
    /// gain or lose runtime code between versions.
    /// </summary>
    [Serializable]
    internal sealed class AddonCatalogVersion
    {
        public string version;
        public string channel;
        public bool hasRuntime;
        public string coreVersionRange;
        public string runtime;
        public string unityVersion;
        public string releaseNotes;
        public string publishedAt;
        public string sha256;
        public long sizeBytes;
        public string manifestUrl;
        public AddonDependency[] dependencies;
        public ExternalAddonPrerequisite[] externalPrerequisites;
    }

    /// <summary>One required Molca add-on with minimum-version/same-major semantics.</summary>
    [Serializable]
    internal sealed class AddonDependency
    {
        public string id;
        public string minimumVersion;
        public string maximumMajorExclusive;
    }

    /// <summary>A reviewed project-owned Unity package required by an add-on.</summary>
    [Serializable]
    internal sealed class ExternalAddonPrerequisite
    {
        public string packageId;
        public string source;
        public string spec;
        public string resolvedCommit;
        public string publisher;
    }

    /// <summary>Wire response containing a signed manifest compact token.</summary>
    [Serializable]
    internal sealed class AddonManifestResponse
    {
        public int schemaVersion;
        public string manifestToken;
    }

    /// <summary>
    /// Claims signed by the distribution service for one immutable add-on artifact.
    /// Field names deliberately mirror the server JSON for <see cref="UnityEngine.JsonUtility"/>.
    /// </summary>
    [Serializable]
    internal sealed class AddonManifest
    {
        public int schemaVersion;
        public string kind;
        public string id;
        public string name;
        public string description;
        public string publisher;
        public string version;
        public string sha256;
        public long sizeBytes;
        public bool hasRuntime;
        public string coreVersionRange;
        public string runtime;

        /// <summary>True when the pack ships a precompiled assembly, which pins it to one runtime.</summary>
        public bool precompiled;

        public string unityVersion;
        public string releaseNotes;
        public string publishedAt;
        public AddonDependency[] dependencies;
        public ExternalAddonPrerequisite[] externalPrerequisites;
        public bool offline;
        public string downloadUrl;
        public string downloadExpiresAt;
    }

    [Serializable]
    internal sealed class AddonApprovalRequest
    {
        public string projectBinding;
        public string id;
        public string version;
        public string coreVersion;
        public string runtime;
        public string channel;
    }

    [Serializable]
    internal sealed class AddonApprovalResponse
    {
        public AddonResolvedPackage[] selected;
        public string[] order;
    }

    [Serializable]
    internal sealed class AddonResolvedPackage
    {
        public string id;
        public string name;
        public string version;
        public string channel;
        public bool requested;
    }

    /// <summary>
    /// A manifest that has passed keyring signature, schema, identity, compatibility, and host checks.
    /// Installer APIs accept this type so unverified server JSON cannot accidentally reach disk.
    /// </summary>
    internal sealed class VerifiedAddonManifest
    {
        internal VerifiedAddonManifest(AddonManifest manifest, string keyId, string token)
        {
            Manifest = manifest;
            KeyId = keyId;
            Token = token;
        }

        public AddonManifest Manifest { get; }
        public string KeyId { get; }
        public string Token { get; }
    }

    /// <summary>Non-throwing result used by add-on network and install operations.</summary>
    internal readonly struct AddonOperationResult<T>
    {
        private AddonOperationResult(bool success, T value, string error)
        {
            Success = success;
            Value = value;
            Error = error;
        }

        public bool Success { get; }
        public T Value { get; }
        public string Error { get; }

        public static AddonOperationResult<T> Ok(T value) => new AddonOperationResult<T>(true, value, null);
        public static AddonOperationResult<T> Fail(string error) => new AddonOperationResult<T>(false, default, error);
    }

    /// <summary>Outcome of an install/remove operation, including a recoverable backup location when relevant.</summary>
    internal readonly struct AddonInstallResult
    {
        private AddonInstallResult(bool success, string message, string recoveryPath)
        {
            Success = success;
            Message = message;
            RecoveryPath = recoveryPath;
        }

        public bool Success { get; }
        public string Message { get; }
        public string RecoveryPath { get; }

        public static AddonInstallResult Ok(string message, string recoveryPath = null) =>
            new AddonInstallResult(true, message, recoveryPath);
        public static AddonInstallResult Fail(string message) => new AddonInstallResult(false, message, null);
    }
}
