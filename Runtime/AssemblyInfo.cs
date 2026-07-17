using System.Runtime.CompilerServices;

// Lets the EditMode test assembly exercise internal members (e.g. BudgetMetricCollector,
// Step.NotifyPause/NotifyResume equivalents) without widening the public API.
[assembly: InternalsVisibleTo("Molca.Core.Tests")]
