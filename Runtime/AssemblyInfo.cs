using System.Runtime.CompilerServices;

// Lets the EditMode test assembly exercise internal members (e.g. BudgetMetricCollector,
// Step.NotifyPause/NotifyResume equivalents) without widening the public API.
[assembly: InternalsVisibleTo("Molca.Core.Tests")]

// Same grant for the PlayMode suite, which needs ColorThemeVariant.SetValue and
// ColorModule.RuntimeProviderOverride to stand up a synthetic theme set and to save/restore the
// static legacy-provider override around each test. Both are internal so a consumer project cannot
// reach them; a test assembly inside the package is not a consumer.
[assembly: InternalsVisibleTo("Molca.Core.PlayModeTests")]

// Lets Core's own editor layer author generated assets. Same reason the Networking assembly grants
// it: authoring is Molca.Editor's job, and a consumer project must not be able to rewrite generated
// configuration through the runtime API. ColorThemeManifest.Populate is internal for exactly this —
// the manifest is derived output written by ColorThemeUssGenerator, and a public mutator on a
// ScriptableObject would invite the runtime writes the framework forbids.
[assembly: InternalsVisibleTo("Molca.Editor")]
