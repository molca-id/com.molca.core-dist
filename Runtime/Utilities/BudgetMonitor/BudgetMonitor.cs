using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Attributes;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Molca.Utilities
{
    /// <summary>
    /// Runtime performance budget overlay. Displays FPS, memory, scene complexity, and rendering stats.
    /// Rendered via UI Toolkit. Attach as a child component on the RuntimeManager prefab.
    /// </summary>
    /// <remarks>
    /// Editor-only metrics (Draw Calls, Batches, Triangles) use <see cref="UnityEditor.UnityStats"/> and
    /// will not appear in builds. All other metrics are available in both Editor and builds.
    /// Toggle visibility at runtime with <c>Ctrl + [toggleKey]</c>.
    /// </remarks>
    public class BudgetMonitor : RuntimeSubsystem
    {
        [InfoBox("Editor-only metrics (Draw Calls, Batches, Triangles) use Unity's Profiler API and won't appear in builds.", InfoBoxType.Info)]
        [Header("Display Settings")]
        [SerializeField] private bool _showMonitor = true;
        [SerializeField] private bool _showWarningsOnly = false;
        [SerializeField] private Key _toggleKey = Key.M;

        [Header("Position & Layout")]
        [Tooltip("Pixel offset from the anchor corner")]
        [SerializeField] private Vector2 _position = new Vector2(10, 10);
        [SerializeField] private UIAnchor _anchor = UIAnchor.TopLeft;

        [Header("Update Settings")]
        [Tooltip("How often metrics are refreshed, in seconds")]
        [SerializeField] private float _updateInterval = 0.5f;

        [Header("Budget Settings")]
        [SerializeField] private BudgetSettings _budgetSettings;

        [Header("UI Toolkit")]
        [Tooltip("PanelSettings asset required by UIDocument. Must be assigned for the overlay to render.")]
        [SerializeField] private PanelSettings _panelSettings;

        private BudgetMetricCollector _collector;
        private BudgetMonitorView _view;
        private readonly Dictionary<string, MetricData> _metrics = new Dictionary<string, MetricData>();

        private float _timeLeft;
        private float _frameCount;
        private float _smoothedFPS;

        // ── Public types ─────────────────────────────────────────────────────────

        public enum UIAnchor { TopLeft, TopRight, BottomLeft, BottomRight, Center }

        /// <summary>Snapshot of a single collected metric.</summary>
        /// <remarks>Value-type — safe to copy out of <see cref="GetMetricsSnapshot"/> from any thread.</remarks>
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
                if (_budgetSettings == null)
                    _budgetSettings = ScriptableObject.CreateInstance<BudgetSettings>();

                _collector = new BudgetMetricCollector(_budgetSettings);

                // AddComponent<BudgetMonitorView> also adds UIDocument via [RequireComponent].
                _view = gameObject.AddComponent<BudgetMonitorView>();
                _view.Initialize(_panelSettings, _anchor, _position, _toggleKey);
                _view.SetVisible(_showMonitor);

                _timeLeft = _updateInterval;
            }
            catch (Exception e)
            {
                Debug.LogError($"[BudgetMonitor] Initialization failed: {e.Message}");
                enabled = false;
            }

            finishCallback.Invoke(this);
        }

        private void Update()
        {
            if (!IsActive) return;

            _frameCount++;
            _timeLeft -= Time.deltaTime;

            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.ctrlKey.isPressed && keyboard[_toggleKey].wasPressedThisFrame)
                _view.ToggleVisibility();

            if (_timeLeft <= 0f)
            {
                _smoothedFPS = Mathf.Lerp(_smoothedFPS, _frameCount / _updateInterval, 0.1f);
                _frameCount = 0;
                _timeLeft = _updateInterval;

                _collector.CollectAll(_metrics, _smoothedFPS);

                var displayed = _showWarningsOnly
                    ? _metrics.Where(m => m.Value.isWarning || m.Value.isCritical)
                               .ToDictionary(m => m.Key, m => m.Value)
                    : (IReadOnlyDictionary<string, MetricData>)_metrics;

                _view.UpdateMetrics(displayed);
            }
        }

        // ── Public API ───────────────────────────────────────────────────────────

        /// <summary>Shows or hides the overlay.</summary>
        public void SetVisible(bool visible) => _view?.SetVisible(visible);

        /// <summary>Toggles overlay visibility.</summary>
        public void ToggleVisibility() => _view?.ToggleVisibility();

        /// <summary>Whether the overlay is currently visible.</summary>
        public bool IsVisible => _view?.IsVisible ?? false;

        /// <summary>
        /// Returns a value-type snapshot of all collected metrics.
        /// Safe to read from any thread.
        /// </summary>
        public Dictionary<string, MetricData> GetMetricsSnapshot() => new(_metrics);

        /// <summary>Returns true if any metric is in warning or critical state.</summary>
        public bool HasWarningsOrErrors() => _metrics.Any(m => m.Value.isWarning || m.Value.isCritical);
    }
}
