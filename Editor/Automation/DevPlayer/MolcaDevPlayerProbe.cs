using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEditor.Networking.PlayerConnection;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking.PlayerConnection;

namespace Molca.Editor.Automation.DevPlayer
{
    /// <summary>
    /// The Editor side of the development-player bridge (§17 Phase 5): enumerates connected development
    /// players and requests a read-only <c>MolcaDevPlayerSnapshot</c> over
    /// <c>EditorConnection</c>, bounded by a timeout. Read-only — it only asks the Player to describe
    /// itself; there is no action or eval path.
    /// </summary>
    public static class MolcaDevPlayerProbe
    {
        /// <summary>Names of the players currently connected to this Editor (empty when none).</summary>
        /// <returns>The connected player names.</returns>
        public static IReadOnlyList<string> ConnectedPlayers()
        {
            try
            {
                return EditorConnection.instance.ConnectedPlayers.Select(p => p.name).ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Sends a probe request to connected players and awaits the first snapshot JSON, or throws
        /// <see cref="TimeoutException"/> if none replies within <paramref name="timeoutMs"/>.
        /// </summary>
        /// <param name="timeoutMs">How long to wait for a reply.</param>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <returns>The raw snapshot JSON the Player sent.</returns>
        /// <exception cref="TimeoutException">If no player replied in time.</exception>
        public static async Awaitable<string> RequestSnapshotJsonAsync(int timeoutMs, CancellationToken cancellationToken)
        {
            var source = new AwaitableCompletionSource<string>();

            UnityAction<MessageEventArgs> handler = args =>
            {
                try { source.TrySetResult(Encoding.UTF8.GetString(args.data ?? Array.Empty<byte>())); }
                catch (Exception ex) { source.TrySetException(ex); }
            };

            EditorConnection.instance.Register(Molca.DevPlayer.MolcaDevPlayerProtocol.ProbeResponseChannel, handler);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(Mathf.Max(100, timeoutMs));
            using var registration = timeoutCts.Token.Register(() => source.TrySetCanceled());

            try
            {
                var correlationId = Guid.NewGuid().ToString();
                EditorConnection.instance.Send(
                    Molca.DevPlayer.MolcaDevPlayerProtocol.ProbeRequestChannel, Encoding.UTF8.GetBytes(correlationId));
                return await source.Awaitable;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"No development player replied within {timeoutMs}ms.");
            }
            finally
            {
                EditorConnection.instance.Unregister(Molca.DevPlayer.MolcaDevPlayerProtocol.ProbeResponseChannel, handler);
            }
        }
    }
}
