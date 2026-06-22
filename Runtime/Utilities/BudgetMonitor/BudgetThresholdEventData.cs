using System.Collections.Generic;

namespace Molca.Utilities
{
    /// <summary>
    /// Payload for the budget threshold-crossing events dispatched by <see cref="BudgetMonitor"/>
    /// (Sprint 54): a metric entered or left the critical state. Lets an automated playtest or CI harness
    /// react to a budget regression instead of relying on someone watching the HUD.
    /// </summary>
    public sealed class BudgetThresholdEventData
    {
        /// <summary>The metric that triggered this event (e.g. <c>"Draw Calls"</c>), or <c>null</c> for an aggregate recovery.</summary>
        public string MetricName { get; }

        /// <summary>The metric's current value at the crossing.</summary>
        public float CurrentValue { get; }

        /// <summary>The metric's budgeted maximum.</summary>
        public float MaxValue { get; }

        /// <summary>True when the metric crossed <em>into</em> critical; false on recovery (all metrics back under budget).</summary>
        public bool EnteredCritical { get; }

        /// <summary>Every metric currently in the critical state, for context.</summary>
        public IReadOnlyList<string> CriticalMetrics { get; }

        /// <summary>Creates a threshold-crossing payload.</summary>
        public BudgetThresholdEventData(string metricName, float currentValue, float maxValue, bool enteredCritical,
            IReadOnlyList<string> criticalMetrics)
        {
            MetricName = metricName;
            CurrentValue = currentValue;
            MaxValue = maxValue;
            EnteredCritical = enteredCritical;
            CriticalMetrics = criticalMetrics ?? System.Array.Empty<string>();
        }
    }
}
