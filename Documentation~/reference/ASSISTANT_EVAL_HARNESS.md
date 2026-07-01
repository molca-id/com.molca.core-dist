# Assistant Weak-Model Eval Harness (Sprint 70)

Sprints 68.9, 69, and 69.8 all improved small-model coherence in the in-editor assistant, but every
judgment was made by eyeballing a single live transcript. This harness makes those judgments
**measured**: it drives a *real* `AssistantChatController` turn against expected outcomes and rolls the
result into a scorecard, so the next weak-model change (or a re-run of the 69.8 A/B) is scored, not
guessed.

It lives entirely in the test assembly under
`Packages/com.molca.core/Tests/Editor/Assistant/Eval/` — there is **no production behavior change**. The
harness reuses the existing test seams (the injected provider factory, `FakeLlmProvider`,
`PromptUserAsyncOverride`, `ConfirmActionInModeAsyncOverride`) and drives the shipping `SendAsync` path so
it measures what ships.

## Two tiers

- **Deterministic replay** (`AssistantEvalTests`) — a scripted `FakeLlmProvider` emits canned model
  responses per scenario, driving a real controller turn against a fake instrumented registry
  (`EvalToolProvider`). Fast, offline, CI-safe, and asserted **fully green**.
- **Opt-in live** (`AssistantEvalLiveTests`) — the same scenarios run against a real model (the configured
  project `AssistantSettings`, typically a local Ollama endpoint). Gated by the `MOLCA_EVAL_LIVE`
  environment variable and **skipped by default / in CI**. It emits the same scorecard but asserts no pass
  rate (a real weak model is expected to fail some dimensions).

## Score dimensions

Tool-execution scenarios:

| Dimension | Passes when |
|---|---|
| `ToolSelected` | the expected primary tool executed at least once |
| `ArgsConcrete` | the executed tool's arguments have no placeholder tokens (reuses the Sprint-69 `DetectPlaceholderArguments` guard) |
| `ExecutedOk` | the tool ran without an error result |
| `ResultReported` | the final visible answer contains the expected substrings |
| `NoLoop` | the turn finished without a loop-break / round-cap stop |

Comprehension scenarios (explanatory questions about Molca's own systems):

| Dimension | Passes when |
|---|---|
| `Grounded` | a grounding tool (`molca_kg_query` / `molca_read_source`) was queried before answering |
| `SymbolsCited` | the answer cites the scenario's expected **real** symbols/paths |
| `NoFabrication` | the answer contains none of the scenario's forbidden (fabricated) symbols |
| `ResultReported`, `NoLoop` | as above |

The confabulation guard (`comprehension-scene-complexity`) explicitly forbids the invented
`SceneBudgetContext` / `YamlDotNet` / `CharacterAnalyzer` architecture a weak model narrated for "check
scene complexity".

## Adding a scenario

Scenarios are pure data. Append an `EvalScenario` to `GoldenScenarios.All` (tool-execution or
comprehension). A scenario names its user turn(s), the scripted model steps (`EvalStep.Call` /
`EvalStep.Answer`, rendered per transport automatically), the action tools to allowlist, and the
expectations for its declared `Dimensions`.

```csharp
new EvalScenario
{
    Id = "create-primitive",
    Category = EvalCategory.ToolExecution,
    UserTurns = { "add a sphere to the active scene" },
    ActionTools = { EvalToolProvider.CreateToolName },
    ExpectedTool = EvalToolProvider.CreateToolName,
    ScriptedSteps =
    {
        EvalStep.Call(EvalToolProvider.CreateToolName, ("name", "Sphere"), ("primitive", "Sphere")),
        EvalStep.Answer("Created a Sphere primitive in the active scene.")
    },
    ExpectedAnswerSubstrings = { "Sphere" },
    Dimensions = { EvalDimension.ToolSelected, EvalDimension.ArgsConcrete,
                   EvalDimension.ExecutedOk, EvalDimension.ResultReported, EvalDimension.NoLoop }
}
```

For a comprehension scenario, set `Category = EvalCategory.Comprehension`, list `GroundingTools`,
`ExpectedSymbols` (drawn from the current source/graph so the check stays truthful), and
`ForbiddenSymbols`, and score on `Grounded` / `SymbolsCited` / `NoFabrication`.

Every scenario in `GoldenScenarios.All` is automatically run on **both** transports
(`FunctionCalling` and `Text`) by the deterministic fixture.

## Running

- **Deterministic (default):** run the `AssistantEvalTests` fixture in the Unity Test Runner (EditMode).
  Each scenario×transport is a `TestCaseSource` case; `DeterministicSuite_IsGreenAndReportsNoRegressions`
  logs the full scorecard via `EvalReport.Render` and fails on any regression against the all-green
  baseline.
- **Live (opt-in):** set `MOLCA_EVAL_LIVE=1` in the environment, configure the project Assistant
  settings' provider/model/endpoint, then run `AssistantEvalLiveTests`. The scorecard is logged to the
  console.

## Reading the scorecard

`EvalReport.Render` prints one line per scenario×transport (`PASS`/`FAIL`, dims passed, model rounds),
the failing dimensions with a short reason, then per-transport × per-dimension pass rates and an
aggregate. Comparing the `Text` and `FunctionCalling` columns quantifies the text protocol's benefit for
weak models rather than asserting it.
