namespace Molca.Networking.Diagnostics
{
    /// <summary>
    /// A streaming provider's connection state, in the two terms every telemetry surface actually needs:
    /// whether the stream is up, and what it is doing.
    /// </summary>
    /// <remarks>
    /// This exists so telemetry stops discovering provider state by reflection (plan §7.12). The Hub
    /// previously read a <c>ConnectionStatus</c> property off <see cref="System.Object"/> because the
    /// WebSocket and Socket.IO providers only compile under <c>MOLCA_WEBSOCKET</c> /
    /// <c>MOLCA_SOCKETIO</c> and the editor assembly cannot name types that may not exist. An interface
    /// declared here is always compiled, so an optional provider can implement it inside its own
    /// <c>#if</c> and the reader just does a type test.
    /// <para>
    /// Deliberately two members and no state machine. The providers each own a different notion of
    /// "connecting" today, and inventing a shared enum here would mean either lossy mapping or editing
    /// every status assignment in three providers that cannot all be compiled in one project. The unified
    /// session model — where reconnect state, backoff, and diagnostics are shared rather than
    /// re-implemented per protocol — is Phase 6's job (plan §6.7).
    /// </para>
    /// <para>
    /// <see cref="StreamStatus"/> is shown to a developer in the editor and may name a host or a close
    /// reason. It is not projected into a remote session and must never carry a credential.
    /// </para>
    /// </remarks>
    public interface INetworkStreamStatus
    {
        /// <summary>Whether the stream is currently connected.</summary>
        bool IsStreamConnected { get; }

        /// <summary>
        /// A short human-readable state, e.g. <c>Connected</c>, <c>Reconnecting (attempt 3)</c>, or
        /// <c>Error: …</c>. Never <c>null</c>.
        /// </summary>
        string StreamStatus { get; }
    }
}
