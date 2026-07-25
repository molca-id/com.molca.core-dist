using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Molca.Telemetry;

namespace Molca.Licensing
{
    /// <summary>
    /// Emits a one-shot <c>license.heartbeat</c> telemetry event at runtime carrying the licensee
    /// identity baked into the build. This is the <b>soft</b> runtime evidence layer complementing the
    /// build-time developer gate: it records that a licensed build ran, into whatever telemetry sink the
    /// project has configured. It never blocks, never contacts the license server directly, and is a
    /// complete no-op when there is no build stamp (e.g. in the editor) or telemetry is disabled.
    /// </summary>
    /// <remarks>
    /// The stamp is written into <c>Resources/MolcaLicenseStamp</c> only for actual player builds
    /// (see the editor-side stamp writer) and removed after the build, so <see cref="Resources.Load"/>
    /// returns <c>null</c> in the editor and this does nothing there.
    /// </remarks>
    public static class LicenseHeartbeat
    {
        // Bounds the wait for bootstrap so a build with no RuntimeManager can't leave a frame loop pending.
        private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(60);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            // Fire-and-forget: the heartbeat owns its exceptions and honors a timeout (async contract §5).
            _ = EmitAsync();
        }

        private static async Awaitable EmitAsync()
        {
            try
            {
                LicenseStampData stamp = LicenseStamp.Current;
                if (stamp == null)
                    return; // No stamp — editor play mode or an unlicensed/unstamped build.

                using var cts = new CancellationTokenSource(InitTimeout);
                await RuntimeManager.WaitForInitialization(cts.Token);

                var telemetry = RuntimeManager.GetSubsystem<TelemetrySubsystem>();
                if (telemetry == null || !telemetry.IsEnabled)
                    return; // Telemetry not present/enabled — soft layer, so simply nothing to emit.

                telemetry.Track("license.heartbeat", new Dictionary<string, object>
                {
                    { "licenseeId", stamp.licenseeId },
                    { "coreVersion", stamp.coreVersion ?? string.Empty },
                });
            }
            catch (OperationCanceledException)
            {
                // Bootstrap timed out or was torn down — nothing to report.
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[License] Heartbeat skipped: {e.Message}");
            }
        }
    }
}
