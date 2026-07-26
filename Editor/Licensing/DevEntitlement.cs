using System;

namespace Molca.Editor.Licensing
{
    /// <summary>
    /// The claims carried by a signed developer entitlement token. Mirrors the payload the
    /// license server signs, so <see cref="UnityEngine.JsonUtility"/> can deserialize it directly.
    /// </summary>
    /// <remarks>Field names and casing must match the server's payload exactly.</remarks>
    [Serializable]
    internal class DevEntitlementPayload
    {
        /// <summary>Payload schema version.</summary>
        public int v;

        /// <summary>Stable licensee id (Workspace domain, or the individual email).</summary>
        public string licenseeId;

        /// <summary>Control-plane user id used for current membership authorization.</summary>
        public string userId;

        /// <summary>Customer membership role hint. Server-side current membership remains authoritative.</summary>
        public string role;

        /// <summary>The signed-in developer's verified email.</summary>
        public string email;

        /// <summary>Google Workspace hosted-domain claim, or empty for consumer accounts.</summary>
        public string hd;

        /// <summary>Stable Google account id.</summary>
        public string sub;

        /// <summary>Machine this entitlement was issued for (device unique identifier).</summary>
        public string machineId;

        /// <summary>Core package version at activation time.</summary>
        public string coreVersion;

        /// <summary>Issued-at, unix seconds.</summary>
        public long iat;

        /// <summary>Expiry, unix seconds.</summary>
        public long exp;

        /// <summary>The UTC expiry as a <see cref="DateTime"/>.</summary>
        public DateTime ExpiresAtUtc => DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
    }

    /// <summary>The verdict of evaluating a stored/injected entitlement against this machine and clock.</summary>
    internal enum DevLicenseStatus
    {
        /// <summary>No entitlement token is present.</summary>
        Missing,

        /// <summary>The token is present, correctly signed, unexpired, and bound to this machine.</summary>
        Valid,

        /// <summary>The signature failed or the token is malformed (tampered or corrupt).</summary>
        Invalid,

        /// <summary>The signature is good but the entitlement has expired (or is within the skew window).</summary>
        Expired,

        /// <summary>The signature is good but the entitlement was issued for a different machine.</summary>
        WrongMachine,
    }
}
