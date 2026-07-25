using System.Linq;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// Serializes command metadata for the <c>capabilities</c> / <c>describe</c> surfaces (§10, §12): id,
    /// category, classification, mode, reversibility, resource claims, and the input schema. Pure; used by
    /// every transport that lists or documents commands.
    /// </summary>
    public static class MolcaCommandDescriptors
    {
        /// <summary>Serializes one command definition to its descriptor JSON object.</summary>
        /// <param name="command">The command to describe.</param>
        /// <returns>A <see cref="JObject"/> describing the command.</returns>
        public static JObject Describe(MolcaCommandDefinition command)
        {
            JToken inputSchema;
            try { inputSchema = JToken.Parse(command.InputSchemaJson); }
            catch (Newtonsoft.Json.JsonException) { inputSchema = new JObject(); }

            return new JObject
            {
                ["id"] = command.Id,
                ["displayName"] = command.DisplayName,
                ["description"] = command.Description,
                ["category"] = command.Category,
                ["mode"] = command.Mode.ToString(),
                ["kind"] = command.Kind.ToString(),
                ["reversibility"] = MolcaCommandResult.WireStatusName(command.Reversibility.ToString()),
                ["retryClassification"] = MolcaCommandResult.WireStatusName(MolcaRetryPolicy.Classify(command).ToString()),
                ["resourceClaims"] = new JArray(command.ResourceClaims.Select(c => c.ToString())),
                ["outputSchemaVersion"] = command.OutputSchemaVersion,
                ["executionTimeoutMs"] = command.ExecutionTimeoutMs,
                ["supportsCancellation"] = command.SupportsCancellation,
                ["requiresConfirmation"] = command.RequiresConfirmation,
                ["safeInBatchMode"] = command.SafeInBatchMode,
                ["safeAgainstDevelopmentPlayer"] = command.SafeAgainstDevelopmentPlayer,
                ["isAsync"] = command.IsAsync,
                ["isMutating"] = command.IsMutating,
                ["inputSchema"] = inputSchema
            };
        }

        /// <summary>Serializes a full capability listing.</summary>
        /// <param name="kernel">The kernel to enumerate.</param>
        /// <returns>A JSON array of command descriptors.</returns>
        public static JArray Capabilities(MolcaAutomationKernel kernel) =>
            new JArray(kernel.Capabilities().Select(Describe));

        /// <summary>
        /// Serializes a pre-execution plan preview (§13, Phase 4): what would happen if the command ran now
        /// — the mode/policy gates it must clear, whether confirmation is required, its resource claims,
        /// reversibility, and retry classification — <em>without</em> running it. <c>wouldRun</c> is the
        /// bottom line (mode satisfied and authorized); <c>blockers</c> lists why not, in caller terms.
        /// </summary>
        /// <param name="command">The command being previewed.</param>
        /// <param name="decision">The policy decision for the command under the active profile.</param>
        /// <param name="modeOk">Whether the editor's play state satisfies the command's mode.</param>
        /// <param name="modeMessage">The mode-mismatch message when <paramref name="modeOk"/> is false, else null.</param>
        /// <param name="activeProfile">The active policy profile name.</param>
        /// <returns>A <see cref="JObject"/> describing the plan.</returns>
        public static JObject DescribePlan(
            MolcaCommandDefinition command, MolcaAuthorizationDecision decision,
            bool modeOk, string modeMessage, string activeProfile)
        {
            var retry = MolcaRetryPolicy.Classify(command);
            bool wouldRun = modeOk && decision.Allowed;

            var blockers = new JArray();
            if (!modeOk && !string.IsNullOrEmpty(modeMessage)) blockers.Add(modeMessage);
            if (!decision.Allowed && !string.IsNullOrEmpty(decision.Message)) blockers.Add(decision.Message);

            return new JObject
            {
                ["command"] = command.Id,
                ["displayName"] = command.DisplayName,
                ["kind"] = command.Kind.ToString(),
                ["mode"] = command.Mode.ToString(),
                ["reversibility"] = MolcaCommandResult.WireStatusName(command.Reversibility.ToString()),
                ["retryClassification"] = MolcaCommandResult.WireStatusName(retry.ToString()),
                ["retryRationale"] = MolcaRetryPolicy.Explain(retry),
                ["resourceClaims"] = new JArray(command.ResourceClaims.Select(c => c.ToString())),
                ["activeProfile"] = activeProfile,
                ["modeSatisfied"] = modeOk,
                ["requiresConfirmation"] = command.RequiresConfirmation,
                ["authorization"] = new JObject
                {
                    ["allowed"] = decision.Allowed,
                    ["needsConfirmation"] = decision.NeedsConfirmation,
                    ["code"] = decision.Code,
                    ["message"] = decision.Message
                },
                ["wouldRun"] = wouldRun,
                ["needsConfirmationToRun"] = wouldRun && decision.NeedsConfirmation,
                ["blockers"] = blockers
            };
        }

        /// <summary>
        /// Serializes a run's live state for the <c>run-status</c> surface (§16): status, transport,
        /// timings, latest progress, and the final result once terminal.
        /// </summary>
        /// <param name="handle">The run handle.</param>
        /// <returns>A <see cref="JObject"/> describing the run.</returns>
        public static JObject DescribeRun(MolcaRunHandle handle)
        {
            var o = new JObject
            {
                ["runId"] = handle.RunId,
                ["command"] = handle.CommandId,
                ["transport"] = handle.Transport.ToString(),
                ["status"] = MolcaCommandResult.WireStatusName(handle.Status.ToString()),
                ["isTerminal"] = handle.IsTerminal,
                ["createdAtUtc"] = handle.CreatedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["startedAtUtc"] = handle.StartedAtUtc?.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["completedAtUtc"] = handle.CompletedAtUtc?.ToString("yyyy-MM-ddTHH:mm:ssZ")
            };

            if (handle.Progress.HasValue)
            {
                var p = handle.Progress.Value;
                o["progress"] = new JObject
                {
                    ["fraction"] = p.Fraction,
                    ["message"] = p.Message,
                    ["stepIndex"] = p.StepIndex,
                    ["stepCount"] = p.StepCount,
                    ["stepName"] = p.StepName
                };
            }

            o["result"] = handle.Result != null ? handle.Result.ToJson() : null;
            return o;
        }
    }
}
