using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>One research sub-task request — a prompt and optional extra scoping (Sprint 56).</summary>
    public readonly struct SubtaskRequest
    {
        /// <summary>The research question for the sub-agent.</summary>
        public string Prompt { get; }

        /// <summary>Optional extra scoping appended to the prompt; may be null.</summary>
        public string Focus { get; }

        /// <summary>Creates a sub-task request.</summary>
        public SubtaskRequest(string prompt, string focus)
        {
            Prompt = prompt;
            Focus = focus;
        }
    }

    /// <summary>The result of a read-only research sub-agent run (Sprint 56).</summary>
    public sealed class SubAgentResult
    {
        /// <summary>The sub-agent's final answer — the only thing returned to the main chat's context.</summary>
        public string Digest { get; set; } = string.Empty;

        /// <summary>One line per tool the sub-agent ran, for the auditable transcript row (not sent to the main model).</summary>
        public IReadOnlyList<string> Steps { get; set; } = Array.Empty<string>();

        /// <summary>Prompt (input) tokens the sub-agent billed, summed across its rounds.</summary>
        public long InputTokens { get; set; }

        /// <summary>Completion (output) tokens the sub-agent produced (vendor count, else estimated).</summary>
        public long OutputTokens { get; set; }

        /// <summary>True when the sub-agent hit its round cap before finishing — the digest is partial.</summary>
        public bool Truncated { get; set; }
    }

    /// <summary>
    /// A bounded, <b>read-only</b> research sub-agent (Sprint 56): a throwaway turn loop that runs
    /// model→tool→model against the read-only tool subset and returns only a short digest, so verbose tool
    /// output (file reads, KG dumps, scene scans) never enters the main chat's history. Built on the
    /// Sprint-51 provider-factory seam, so it is deterministically testable with a fake provider.
    /// </summary>
    /// <remarks>
    /// <para><b>Read-only is the whole safety story.</b> Sub-agents are offered only the read-only tool specs
    /// (no Action tools), so concurrent sub-agents never collide on the process-global Unity Undo stack, the
    /// single pending-prompt source, or shared static caches — interleaving is safe.</para>
    /// <para>Deliberately lean: its own bounded history, no pinned context, no retrieval, no compaction.</para>
    /// </remarks>
    internal static class AssistantSubAgent
    {
        /// <summary>System prompt for a research sub-agent — focused on answering concisely from tool evidence.</summary>
        internal const string SubAgentSystemPrompt =
            "You are a focused research sub-agent for the Molca in-editor assistant. You are given one " +
            "research question about this Unity project / the Molca framework. Use the available read-only " +
            "tools (scene/asset/component introspection, knowledge-graph query, source reading, status) to " +
            "gather evidence, then reply with a concise, self-contained digest that directly answers the " +
            "question — lead with the answer, cite concrete file/type names and src=path:Lnn references, and " +
            "keep it short. You cannot modify anything and have no UI to ask the user; never request actions " +
            "or clarification — answer from what the tools return. Do not narrate tool calls.";

        /// <summary>
        /// Runs one sub-agent to completion (or its round cap) and returns its digest plus token usage.
        /// </summary>
        /// <param name="prompt">The research question to answer.</param>
        /// <param name="focus">Optional extra scoping appended to the prompt; may be null.</param>
        /// <param name="providerFactory">Sprint-51 seam supplying the LLM provider.</param>
        /// <param name="registry">Tool registry; only its read-only tools are offered.</param>
        /// <param name="model">Model id for the sub-agent's requests.</param>
        /// <param name="maxRounds">Round cap; on reaching it the digest is returned with <see cref="SubAgentResult.Truncated"/>.</param>
        /// <param name="maxTokens">Per-response output token ceiling.</param>
        /// <param name="cancellationToken">Cancelled with the parent turn.</param>
        public static async Awaitable<SubAgentResult> RunAsync(
            string prompt, string focus, Func<ILlmProvider> providerFactory, McpToolRegistry registry,
            string model, int maxRounds, int maxTokens, CancellationToken cancellationToken)
        {
            var result = new SubAgentResult();
            if (string.IsNullOrWhiteSpace(prompt))
            {
                result.Digest = "(empty sub-task prompt)";
                return result;
            }

            var provider = providerFactory();
            var tools = AssistantToolBridge.GetToolSpecs(registry, isActionAllowed: null); // read-only only
            var steps = new List<string>();
            var history = new List<LlmMessage>
            {
                LlmMessage.UserText(string.IsNullOrWhiteSpace(focus) ? prompt : $"{prompt}\n\nFocus: {focus}")
            };

            for (var round = 0; round < Math.Max(1, maxRounds); round++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = new LlmRequest
                {
                    System = SubAgentSystemPrompt,
                    Messages = new List<LlmMessage>(history),
                    Tools = tools,
                    Model = model,
                    MaxTokens = maxTokens
                };

                var response = await provider.SendAsync(request, cancellationToken);
                result.InputTokens += response.PromptTokens;
                result.OutputTokens += response.CompletionTokens > 0
                    ? response.CompletionTokens
                    : EstimateTokens(response.Text);

                history.Add(new LlmMessage { Role = LlmRole.Assistant, Text = response.Text, ToolCalls = response.ToolCalls });
                if (!string.IsNullOrWhiteSpace(response.Text))
                    result.Digest = response.Text;

                if (!response.WantsToolUse)
                {
                    result.Steps = steps;
                    return result;
                }

                var resultMsg = new LlmMessage { Role = LlmRole.User };
                foreach (var call in response.ToolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Read-only execution: no confirmer/asker/plan delegates, so any Action tool the model
                    // attempts is refused by the allowlist gate rather than run.
                    var toolResult = await AssistantToolBridge.ExecuteAsync(registry, call, cancellationToken, isActionAllowed: null);
                    resultMsg.ToolResults.Add(toolResult);
                    steps.Add($"{call.Name}: {(toolResult.IsError ? "error" : "ok")}");
                }
                history.Add(resultMsg);

                if (round == Math.Max(1, maxRounds) - 1)
                    result.Truncated = true;
            }

            result.Steps = steps;
            if (result.Truncated && string.IsNullOrWhiteSpace(result.Digest))
                result.Digest = "(sub-task hit its round limit before producing an answer)";
            return result;
        }

        /// <summary>
        /// Runs several sub-agents with bounded concurrency (Sprint 56 "swarm"): tasks are processed in
        /// batches of <paramref name="concurrency"/>, each batch kicked off together (so their network I/O
        /// overlaps) then awaited. Read-only, so interleaving is safe.
        /// </summary>
        public static async Awaitable<SubAgentResult[]> RunManyAsync(
            IReadOnlyList<(string prompt, string focus)> tasks, Func<ILlmProvider> providerFactory,
            McpToolRegistry registry, string model, int maxRounds, int maxTokens, int concurrency,
            CancellationToken cancellationToken)
        {
            var results = new SubAgentResult[tasks?.Count ?? 0];
            if (results.Length == 0) return results;

            var lane = Math.Max(1, concurrency);
            for (var start = 0; start < tasks.Count; start += lane)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var end = Math.Min(start + lane, tasks.Count);

                // Kick the whole batch off first (each runs to its first await), then await them — their
                // provider requests overlap on the editor async context.
                var inFlight = new List<Awaitable<SubAgentResult>>();
                for (var i = start; i < end; i++)
                    inFlight.Add(RunAsync(tasks[i].prompt, tasks[i].focus, providerFactory, registry, model, maxRounds, maxTokens, cancellationToken));

                for (var i = 0; i < inFlight.Count; i++)
                    results[start + i] = await inFlight[i];
            }
            return results;
        }

        private static long EstimateTokens(string text) => string.IsNullOrEmpty(text) ? 0 : text.Length / 4;
    }
}
