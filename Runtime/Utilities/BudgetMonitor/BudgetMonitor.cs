using System;
using System.Collections.Generic;
using Molca.Attributes;
using Molca.Events;
using Molca.Telemetry;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Molca.Utilities
{
    /// <summary>
    /// Runtime performance budget overlay. Displays FPS, memory, scene complexity, and rendering stats,
    /// and (opt-in) raises a typed event when a metric crosses a budget so playtests/CI can gate on it.
    /// Rendered via UI Toolkit. Attach as a child component on the RuntimeManager prefab.
    /// </summary>
    /// <remarks>
    /// Rendering and texture-memory metrics are sourced from <see cref="Unity.Profiling.ProfilerRecorder"/>
    /// counters, so they report in <b>development builds</b> as well as the editor (Sprint 54). FPS uses
    /// <b>unscaled</b> time so a paused game (<c>timeScale = 0</c>) still reads a real frame rate. Toggle
    /// visibility at runtime with <c>Ctrl + [toggleKey]</c> (requires the Input System package).
    /// </remarks>
    public class BudgetMonitor : RuntimeSubsystem
    {
        [InfoBox("Rendering/texture metrics use Unity's ProfilerRecorder and report in development builds, not just the editor.", InfoBoxType.Info)]
        [Header("Display Settings")]
        [SerializeField] private bool _showMonitor = true;
        [SerializeField] private bool _showWarningsOnly = false;
#if ENABLE_INPUT_SYSTEM
        [SerializeField] private Key _toggleKey = Key.M;
#else
        [SerializeField] private KeyCode _toggleKey = KeyCode.M;
#endif

        [Header("Position & Layout")]
        [Tooltip("Pixel offset from the anchor corner")]
        [SerializeField] private Vector2 _position = new Vector2(10, 10);
        [SerializeField] private UIAnchor _anchor = UIAnchor.TopLeft;

        [Header("Update Settings")]
        [Tooltip("How often the cheap metrics (FPS, memory, rendering) are refreshed, in seconds")]
        [SerializeField] private float _updateInterval = 0.5f;

        [Tooltip("How often the expensive scene-composition scan (GameObject/material/mesh counts) runs, in seconds. Also recomputed on scene load.")]
        [SerializeField] private float _sceneScanInterval = 2f;

        [Header("Budget Settings")]
        [Tooltip("Explicit budget to grade against. Leave empty to resolve a platform-matched budget from the list below.")]
        [SerializeField] private BudgetSettings _budgetSettings;

        [Tooltip("Platform-specific budgets (e.g. Mobile/PC/Quest). When no explicit budget is set, the one matching the current platform is chosen.")]
        [SerializeField] private List<BudgetSettings> _platformBudgets = new List<BudgetSettings>();

        [Header("Budget Gate")]
        [Tooltip("Raise a typed event and record telemetry when a metric crosses into/out of critical, even while the HUD is hidden. Lets CI/playtests gate on budget regressions.")]
        [SerializeField] private bool _enableBudgetGate = false;

        [Header("UI Toolkit")]
        [Tooltip("PanelSettings asset required by UIDocument. Must be assigned for the overlay to render.")]
        [SerializeField] private PanelSettings _panelSettings;

        private BudgetMetricCollector _collector;
        private BudgetMonitorView _view;
        private readonly Dictionary<string, MetricData> _metrics = new Dictionary<string, MetricData>();
        // Reused filtered view so the warnings-only path allocates no per-tick dictionary (Sprint 54).
        private readonly Dictionary<string, MetricData> _displayBuffer = new Dictionary<string, MetricData>();
        // Metric keys currently critical, for edge-triggered gate detection (Sprint 54).
        private readonly HashSet<string> _criticalMetrics = new HashSet<string>();

        private float _timeLeft;
        private float _frameCount;
        private float _smoothedFPS;
        private float _nextSceneScanTime;
        private bool _sceneDirty = true;
        private bool _sceneLoadedHooked;

        // ── Public types ─────────────────────────────────────────────────────────

        public enum UIAnchor { TopLeft, TopRight, BottomLeft, BottomRight, Center }

        /// <summary>Snapshot of a single collected metric.</summary>
        public struct MetricData
        {
            public string value;
            public float currentValue;
            public float maxValue;
            public bool isWarning;
            public bool isCritical;
            public MetricType type;
            public string unit;
        }

        public enum MetricType { FPS, Memory, Count, Percentage }

        /// <summary>
        /// Raised on the main thread when a metric enters or leaves the critical state (Sprint 54). A
        /// playtest/CI harness can subscribe to assert "no critical budget crossing"; the budget gate must be
        /// enabled for it to fire.
        /// </summary>
        public event Action<BudgetThresholdEventData> BudgetThresholdCrossed;

        /// <summary>True while at least one metric is in the critical state (Sprint 54).</summary>
        public bool HasCriticalBreach => _criticalMetrics.Count > 0;

        // ── Lifecycle ────────────────────────────────────────────────────────────

        public override void Initialize(Action<IRuntimeSubsystem> finishCallback)
        {
            if (!IsRuntimeValid)
            {
                enabled = false;
                finishCallback.Invoke(this);
                return;
            }

            try
            {
                // Resolve a platform-matched budget when no explicit one is assigned (Sprint 54), so the live
                // monitor and the Sprint-50 scene audit grade against the same budget.
                if (_budgetSettings == null)
                    _budgetSettings = BudgetSettingsProvider.Resolve(_platformBudgets).Settings;

                _collector = new BudgetMetricCollector(_budgetSettings);

                // AddComponent<BudgetMonitorView> also adds UIDocument via [RequireComponent].
                _view = gameObject.AddComponent<BudgetMonitorView>();
                _view.Initialize(_panelSettings, _anchor, _position, _toggleKey.ToString());
                _view.SetVisible(_showMonitor);

                _timeLeft = _updateInterval;
                _nextSceneScanTime = 0f;
                _sceneDirty = true;

                SceneManager.sceneLoaded += OnSceneLoaded;
                _sceneLoadedHooked = true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[BudgetMonitor] Initialization failed: {e.Message}");
                enabled = false;
            }

            finishCallback.Invoke(this);
        }

        public override void Teardown()
        {
            if (_sceneLoadedHooked)
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                _sceneLoadedHooked = false;
            }
            _collector?.Dispose();
            _collector = null;
            base.Teardown();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => _sceneDirty = true;

        private void Update()
        {
            if (!IsActive) return;

            _frameCount++;
            // Unscaled time so a paused game (timeScale = 0) still reports a real frame rate.
            _timeLeft -= Time.unscaledDeltaTime;

            HandleToggleInput();

            if (_timeLeft > 0f) return;

            _smoothedFPS = Mathf.Lerp(_smoothedFPS, _frameCount / _updateInterval, 0.1f);
            _frameCount = 0;
            _timeLeft = _updateInterval;

            // Skip all collection while hidden unless the gate needs the metrics anyway (Sprint 54).
            var visible = _view != null && _view.IsVisible;
            if (!visible && !_enableBudgetGate) return;
            if (_collector == null) return;

            _collector.CollectFrequent(_metrics, _smoothedFPS);

            // Expensive scene-composition scan runs on a slower cadence or right after a scene load.
            if (_sceneDirty || Time.unscaledTime >= _nextSceneScanTime)
            {
                _collector.CollectSceneComposition(_metrics);
                _nextSceneScanTime = Time.unscaledTime + Mathf.Max(_sceneScanInterval, _updateInterval);
                _sceneDirty = false;
            }

            if (_enableBudgetGate) EvaluateGate();

            if (visible) _view.UpdateMetrics(BuildDisplay());
        }

        /// <summary>Reads the toggle key (Input System only); legacy-input projects compile without a runtime toggle.</summary>
        private void HandleToggleInput()
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.ctrlKey.isPressed && keyboard[_toggleKey].wasPressedThisFrame)
                _view?.ToggleVisibility();
#endif
        }

        /// <summary>Returns the metric set to display, filtered to warnings/criticals when configured, reusing a buffer.</summary>
        private IReadOnlyDictionary<string, MetricData> BuildDisplay()
        {
            if (!_showWarningsOnly) return _metrics;

            _displayBuffer.Clear();
            foreach (var kvp in _metrics)
                if (kvp.Value.isWarning || kvp.Value.isCritical)
                    _displayBuffer[kvp.Key] = kvp.Value;
            return _displayBuffer;
        }

        /// <summary>
        /// Edge-triggered budget gate (Sprint 54): dispatches a typed event + telemetry when a metric enters
        /// critical, and a single recovery event when the last critical metric clears.
        /// </summary>
        private void EvaluateGate()
        {
            var hadCritical = _criticalMetrics.Count > 0;

            // Detect newly-critical metrics.
            foreach (var kvp in _metrics)
            {
                if (!kvp.Value.isCritical) continue;
                if (_criticalMetrics.Add(kvp.Key))
                    RaiseThreshold(new BudgetThresholdEventData(
                        kvp.Key, kvp.Value.currentValue, kvp.Value.maxValue, enteredCritical: true, CriticalSnapshot()),
                        EventConstants.Performance.BudgetCritical);
            }

            // Drop metrics that are no longer critical (or no longer present).
            _criticalMetrics.RemoveWhere(key => !_metrics.TryGetValue(key, out var m) || !m.isCritical);

            // One aggregate recovery event when the last critical metric clears.
            if (hadCritical && _criticalMetrics.Count == 0)
                RaiseThreshold(new BudgetThresholdEventData(null, 0f, 0f, enteredCritical: false, Array.Empty<string>()),
                    EventConstants.Performance.BudgetRecovered);
        }

        private string[] CriticalSnapshot()
        {
            var arr = new string[_criticalMetrics.Count];
            _criticalMetrics.CopyTo(arr);
            return arr;
        }

        private void RaiseThreshold(BudgetThresholdEventData data, string eventName)
        {
            try { BudgetThresholdCrossed?.Invoke(data); }
            catch (Exception e) { Debug.LogError($"[BudgetMonitor] BudgetThresholdCrossed handler threw: {e.Message}"); }

            var dispatcher = RuntimeManager.GetSubsystem<Molca.Events.EventDispatcher>();
            dispatcher?.DispatchEvent(eventName, data);

            var telemetry = RuntimeManager.GetSubsystem<TelemetrySubsystem>();
            telemetry?.Track(eventName, new Dictionary<string, object>
            {
                ["metric"] = data.MetricName ?? "(recovered)",
                ["current"] = data.CurrentValue,
                ["max"] = data.MaxValue,
                ["entered"] = data.EnteredCritical
            });
        }

        // ── Public API ───────────────────────────────────────────────────────────

        /// <summary>Shows or hides the overlay.</summary>
        public void SetVisible(bool visible) => _view?.SetVisible(visible);

        /// <summary>Toggles overlay visibility.</summary>
        public void ToggleVisibility() => _view?.ToggleVisibility();

        /// <summary>Whether the overlay is currently visible.</summary>
        public bool IsVisible => _view?.IsVisible ?? false;

        /// <summary>
        /// Returns a value-type snapshot of all collected metrics. <b>Main thread only</b> — the backing
        /// dictionary is mutated on the main thread during collection (the returned copy is then safe to read).
        /// </summary>
        public Dictionary<string, MetricData> GetMetricsSnapshot() => new(_metrics);

        /// <summary>Returns true if any metric is in warning or critical state.</summary>
        public bool HasWarningsOrErrors()
        {
            foreach (var kvp in _metrics)
                if (kvp.Value.isWarning || kvp.Value.isCritical) return true;
            return false;
        }
    }
}
