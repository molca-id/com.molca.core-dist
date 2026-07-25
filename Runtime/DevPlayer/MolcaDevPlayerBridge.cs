#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System.Text;
using UnityEngine;
using UnityEngine.Networking.PlayerConnection;

namespace Molca.DevPlayer
{
    /// <summary>
    /// The opt-in, development-only runtime bridge (§17 Phase 5). It listens on
    /// <see cref="MolcaDevPlayerProtocol.ProbeRequestChannel"/> for an Editor probe request and replies
    /// with a read-only <see cref="MolcaDevPlayerSnapshot"/>, letting a QA/dev build be observed and
    /// smoke-tested locally without any action or eval surface.
    /// </summary>
    /// <remarks>
    /// Compiled <b>only</b> into development builds and the Editor (the <c>DEVELOPMENT_BUILD || UNITY_EDITOR</c>
    /// guard). It cannot exist in a production Player, satisfying the Phase 5 rejection criterion at the
    /// only place that is airtight — compilation. It self-installs via
    /// <see cref="RuntimeInitializeOnLoadMethodAttribute"/>, keeps only a small tally of recent error logs
    /// (never their contents over the wire), and holds no state beyond that counter.
    /// </remarks>
    public static class MolcaDevPlayerBridge
    {
        private static int _recentErrorCount;
        private static bool _installed;

        /// <summary>Installs the bridge after the first scene loads, in development builds and the Editor.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (_installed) return;
            _installed = true;

            Application.logMessageReceivedThreaded += OnLog;
            PlayerConnection.instance.Register(MolcaDevPlayerProtocol.ProbeRequestChannel, OnProbeRequest);
        }

        // Tally errors/exceptions so the snapshot can report a count without ever shipping log text.
        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                System.Threading.Interlocked.Increment(ref _recentErrorCount);
        }

        private static void OnProbeRequest(MessageEventArgs args)
        {
            var correlationId = args.data != null && args.data.Length > 0
                ? Encoding.UTF8.GetString(args.data)
                : string.Empty;

            var snapshot = MolcaDevPlayerProtocol.CaptureSnapshot(correlationId, _recentErrorCount);
            var json = JsonUtility.ToJson(snapshot);
            PlayerConnection.instance.Send(MolcaDevPlayerProtocol.ProbeResponseChannel, Encoding.UTF8.GetBytes(json));
        }
    }
}
#endif
