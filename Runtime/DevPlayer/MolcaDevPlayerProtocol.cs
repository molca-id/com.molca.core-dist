using System;
using UnityEngine;

namespace Molca.DevPlayer
{
    /// <summary>
    /// The shared, read-only wire protocol between the in-Player development bridge
    /// (<c>MolcaDevPlayerBridge</c>) and the Editor probe (§17 Phase 5). The Editor sends a probe request
    /// on <see cref="ProbeRequestChannel"/>; the Player replies with a <see cref="MolcaDevPlayerSnapshot"/>
    /// JSON on <see cref="ProbeResponseChannel"/>. The snapshot is strictly observational — no action or
    /// eval capability is exposed — so this stays within the default read-only policy.
    /// </summary>
    /// <remarks>
    /// The message channels are fixed GUIDs so both sides agree without discovery. The snapshot is a
    /// <c>[Serializable]</c> struct serialized with <see cref="JsonUtility"/> (no Newtonsoft dependency in
    /// the Player); the Editor parses it however it likes.
    /// </remarks>
    public static class MolcaDevPlayerProtocol
    {
        /// <summary>Editor → Player: "send me a diagnostics snapshot." Payload is a correlation id (UTF-8).</summary>
        public static readonly Guid ProbeRequestChannel = new Guid("6d0c1a52-2f7b-4d2e-9a11-b0cade000001");

        /// <summary>Player → Editor: the snapshot JSON (UTF-8) for a prior request.</summary>
        public static readonly Guid ProbeResponseChannel = new Guid("6d0c1a52-2f7b-4d2e-9a11-b0cade000002");

        /// <summary>Schema version of <see cref="MolcaDevPlayerSnapshot"/>.</summary>
        public const int SchemaVersion = 1;

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        /// <summary>
        /// Captures a read-only diagnostics snapshot of the live runtime. Called by the in-Player bridge on
        /// a probe request; also callable in the Editor (for local Play-mode smoke and tests). Compiled
        /// only into development builds and the Editor — never a production Player.
        /// </summary>
        /// <param name="correlationId">Correlates the reply with the Editor's request.</param>
        /// <param name="recentErrorCount">Recent error/exception log count the bridge has tallied.</param>
        /// <returns>The snapshot.</returns>
        public static MolcaDevPlayerSnapshot CaptureSnapshot(string correlationId, int recentErrorCount)
        {
            var resolved = RuntimeManager.GetResolvedInitOrder();
            var discovered = RuntimeManager.GetSubsystems();

            var inactive = new System.Collections.Generic.List<string>();
            foreach (var s in resolved)
                if (s != null && !s.IsActive) inactive.Add(s.GetType().Name);

            return new MolcaDevPlayerSnapshot
            {
                schemaVersion = SchemaVersion,
                correlationId = correlationId ?? string.Empty,
                bootstrapState = RuntimeManager.State.ToString(),
                isReady = RuntimeManager.IsReady,
                subsystemResolvedCount = resolved.Count,
                subsystemDiscoveredCount = discovered.Count,
                inactiveSubsystems = inactive.ToArray(),
                recentErrorCount = recentErrorCount,
                unityVersion = Application.unityVersion,
                isDevelopmentBuild = Debug.isDebugBuild,
                platform = Application.platform.ToString(),
                productName = Application.productName
            };
        }
#endif
    }

    /// <summary>
    /// A read-only snapshot of a running Molca player, sent from the development bridge to the Editor
    /// probe (§11.2 / §17). Serialized with <see cref="JsonUtility"/>; every field is observational.
    /// </summary>
    [Serializable]
    public struct MolcaDevPlayerSnapshot
    {
        /// <summary>Protocol schema version.</summary>
        public int schemaVersion;

        /// <summary>The request correlation id this snapshot answers.</summary>
        public string correlationId;

        /// <summary>The RuntimeManager bootstrap state (NotStarted/Initializing/Ready/Failed).</summary>
        public string bootstrapState;

        /// <summary>Whether bootstrap reached Ready.</summary>
        public bool isReady;

        /// <summary>Number of subsystems in the resolved init order.</summary>
        public int subsystemResolvedCount;

        /// <summary>Number of discovered subsystems.</summary>
        public int subsystemDiscoveredCount;

        /// <summary>Names of subsystems that resolved but are not active.</summary>
        public string[] inactiveSubsystems;

        /// <summary>Recent error/exception log count the bridge tallied.</summary>
        public int recentErrorCount;

        /// <summary>The Player's Unity runtime version.</summary>
        public string unityVersion;

        /// <summary>Whether this Player is a development build (must be true for the bridge to exist).</summary>
        public bool isDevelopmentBuild;

        /// <summary>The runtime platform.</summary>
        public string platform;

        /// <summary>The product name.</summary>
        public string productName;
    }
}
