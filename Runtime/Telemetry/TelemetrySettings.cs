using Molca.Settings;
using UnityEngine;

namespace Molca.Telemetry
{
    /// <summary>
    /// Authored configuration for the telemetry system. Read-only at runtime (no paired
    /// <see cref="SettingState"/>): which sinks are active, the HTTP endpoint, and batching cadence.
    /// Telemetry is <b>off by default</b> — enable it explicitly per project.
    /// </summary>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-settings.png")]
    [CreateAssetMenu(fileName = "TelemetrySettings", menuName = "Molca/Settings/Telemetry Settings", order = 10)]
    public class TelemetrySettings : SettingModule
    {
        [Header("Master")]
        [Tooltip("When false, the TelemetrySubsystem stays inactive and Track() is a no-op.")]
        [SerializeField] private bool _enableTelemetry = false;

        [Header("Sinks")]
        [Tooltip("Log each event to the Unity console (useful in the editor).")]
        [SerializeField] private bool _enableConsoleSink = true;

        [Tooltip("Append events as JSON lines under persistentDataPath/Molca/telemetry.")]
        [SerializeField] private bool _enableFileSink = false;

        [Tooltip("Batch events and POST them to a remote endpoint.")]
        [SerializeField] private bool _enableHttpSink = false;

        [Tooltip("Absolute URL the HTTP batch sink POSTs to. Required when the HTTP sink is enabled.")]
        [SerializeField] private string _httpEndpointUrl = "";

        [Header("Batching")]
        [Tooltip("Flush after this many tracked events accumulate.")]
        [Min(1)]
        [SerializeField] private int _batchSize = 20;

        [Tooltip("Maximum time between automatic flushes, in seconds.")]
        [Min(1f)]
        [SerializeField] private float _flushIntervalSeconds = 30f;

        /// <summary>Master switch. When false the subsystem is inactive.</summary>
        public bool EnableTelemetry => _enableTelemetry;

        /// <summary>Whether the console sink is active.</summary>
        public bool EnableConsoleSink => _enableConsoleSink;

        /// <summary>Whether the file sink is active.</summary>
        public bool EnableFileSink => _enableFileSink;

        /// <summary>Whether the HTTP batch sink is active.</summary>
        public bool EnableHttpSink => _enableHttpSink;

        /// <summary>Endpoint URL for the HTTP batch sink.</summary>
        public string HttpEndpointUrl => _httpEndpointUrl;

        /// <summary>Flush threshold by event count.</summary>
        public int BatchSize => Mathf.Max(1, _batchSize);

        /// <summary>Flush cadence in seconds.</summary>
        public float FlushIntervalSeconds => Mathf.Max(1f, _flushIntervalSeconds);

        // Read-only config: no runtime-mutable state, nothing to load/save.
        /// <inheritdoc/>
        public override void LoadSettings() { }

        /// <inheritdoc/>
        public override void SaveSettings() { }
    }
}
