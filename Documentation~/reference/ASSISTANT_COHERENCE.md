# Assistant Coherence Fixes (Sprint 72)

A controlled live A/B (same goal, repeated, transport switched deliberately) against `gemma4:e4b`
surfaced four distinct, evidence-backed weak-model failures beyond "the model is weak". Three are fixed
directly; the fourth is treated as a **measured experiment** through the Sprint-70 eval harness rather
than a blind change to a cap shared with cloud backends.

## Finding 1 — blank required argument reached execution (fixed)

An Action tool ran with a blank `targets:""` because the placeholder guard only covered the Text
transport. **Fix (72.1):** `AssistantTextToolProtocol.DetectBlankRequiredArgument(schema, args)` reads the
tool's JSON-schema `required` list and flags a missing or empty/whitespace **required string** argument.
It's enforced in `AssistantToolBridge.ExecuteAsync` inside the Action block, so it covers **both
transports** and every execution path (single, batched). The doomed call is intercepted before it runs
and answered with a corrective result steering the model to filtered discovery.

- Covered by: `AssistantCoherenceTests.BlankGuard_*` (pure) and the `blank-required-arg` golden scenario
  (asserts the tool is intercepted, not executed, on both transports).

## Finding 2 — short continuations lost the standing goal (fixed)

Short low-information follow-ups ("continue", "okay then") reliably lost the goal, because
`AppendTurnToolReminder` keyed relevance off the current message's text (a bare acknowledgment matches no
keywords) and `FunctionCalling` never got a reminder at all. **Fix (72.2):** goal-persistence
reinforcement lives at request-build time (`BuildRequestMessages`), **not** gated behind
`useTextToolProtocol`, so both transports benefit. When the current user turn is a short/low-info
acknowledgment (a known continuation phrase or ≤2 words), a transient
`[Continuing the current task: <prior goal>]` note is prepended to the outgoing request only — never
persisted to history. The standing goal is the most recent substantive prior user turn.

- Covered by: `AssistantCoherenceTests.GoalReinforcement_*` (asserts the note is injected for a short
  continuation on both transports, and absent for a substantive turn).

## Finding 3 — no filtered discovery by component type (fixed)

The model correctly diagnosed a real gap: no tool finds GameObjects by component type, and it never used
the existing `nameContains` filter either. **Fix (72.3):** rather than add a tool (Sprint-67 tool-count
discipline), `molca_unity_scene_objects` gains an optional `componentType` filter (substring, case-
insensitive, against attached component type names) alongside `nameContains`. The tool description and the
base system prompt now nudge the model to prefer a filtered discovery call over an unfiltered scene dump
when the user names a specific object or kind, and never to call an action with an undiscovered target.

- Covered by: `AssistantCoherenceTests.SceneObjects_ComponentTypeFilter_ReturnsOnlyMatches` (real
  filtering) and the `filtered-discovery` golden scenario (asserts the `componentType` filter arg is used).

## Finding 4 — large nested tool results (measured, not assumed)

A large, nested tool-result JSON (`molca_validate_sequence`, ~2000 chars) twice and reproducibly caused
the model to abandon a correct result and hallucinate an unrelated tool-family summary, while a smaller,
flatter result was synthesized correctly. This is a real, falsifiable **pattern** — not confirmed causal.

**Methodology (72.5):** the `large-nested-result` golden scenario reproduces the shape — a ~2KB nested
validation payload whose only real finding (`missing Ref Id 'main-valve'`) is buried at step 9 of 12 —
and scores whether the final answer surfaces that deep value (`ResultReported`). Run it on the **live**
tier (`MOLCA_EVAL_LIVE=1`) at the current result size and again at a condensed/flattened size to compare.

**Decision rule:** no production change to `MaxToolResultChars` or any model-scoped condensation ships
unless the measured run shows a real coherence improvement — that setting is shared with cloud backends
and must not regress them on a guess.

**Measured result (provisional null):** on the first live run against `gemma4:e4b`, the
`large-nested-result` scenario **passed on both transports (4/4, `ResultReported` included)** — the model
correctly surfaced the deep `main-valve` finding from the ~2KB nested payload. The abandonment observed
with the original `molca_validate_sequence` result did **not** reproduce here. Caveats: a single run, the
eval payload approximates but is not byte-identical to the real tool's output, and only the current size
was scored (the condensed-size arm was not separately measured because the current size already passed).
**Conclusion: no measured coherence degradation attributable to result size, so — per the decision rule —
no `MaxToolResultChars` or condensation change was made.** Re-run with the exact `molca_validate_sequence`
shape and the condensed-size arm if the abandonment recurs in practice; update this section if a real
effect is ever measured.

**Live-run side findings (for a future sprint, not fixed here):** (a) `delete-by-name` on FunctionCalling
now follows the discover-first nudge — it calls `molca_unity_scene_objects` — but then stalls without
issuing the delete (discovery succeeded, action not completed); (b) `ResultReported` uses literal-substring
matching and under-counts prose answers that report the outcome without echoing the exact token.

## Running the coverage

- Deterministic: `AssistantEvalTests` (golden scenarios, both transports) + `AssistantCoherenceTests`.
- Live experiment: set `MOLCA_EVAL_LIVE=1`, configure the model, run `AssistantEvalLiveTests`, and read the
  `large-nested-result` row in the scorecard at each result size.
