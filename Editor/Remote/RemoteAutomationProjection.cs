using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Molca.Editor.Automation;
using Molca.Editor.Automation.BuiltIn;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Remote
{
    /// <summary>
    /// Pure projection of the automation kernel onto the bounded <c>remoteEditor</c> v1 wire shapes — the
    /// capability catalog, the <c>automation</c> state block, and the <c>run-status</c> envelope. Every
    /// bound the contract states is enforced here, at the Editor boundary, before anything leaves the
    /// machine; the control plane's sanitizer is a second, independent enforcement of the same limits.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="RemoteCompanionFacade"/> because it is the part with no lifecycle and no
    /// Editor-thread dependency beyond reading the kernel, which makes it directly unit-testable
    /// (<c>RemoteAutomationProjectionTests</c>). Two rules are load-bearing and must not be relaxed:
    /// <see cref="MolcaCommandDefinition.InputSchemaJson"/> never leaves the Editor (only a derived
    /// <c>argumentsShape</c> tier does), and command-authored free text is omitted for any command
    /// <see cref="CoreShippedCommands"/> does not vouch for.
    /// </remarks>
    internal static class RemoteAutomationProjection
    {
        internal const int MaxCatalogCommands = 64;
        internal const int MaxActiveRuns = 6;
        internal const int MaxRecentRuns = 10;
        internal const int MaxDiagnostics = 10;

        private const int MaxCommandIdChars = 128;
        private const int MaxDisplayNameChars = 64;
        private const int MaxDescriptionChars = 256;
        private const int MaxCategoryChars = 32;
        private const int MaxResourceClaims = 8;
        private const int MaxProgressMessageChars = 128;
        private const int MaxStepNameChars = 64;
        private const int MaxRefusalMessageChars = 256;
        private const int MaxEvidenceChars = 512;
        private const int MaxDiagnosticMessageChars = 256;
        private const int MaxDiagnosticCodeChars = 64;

        /// <summary>
        /// Commands with a hand-written argument form in the dashboard. A command must be listed here to
        /// reach <c>simple</c>: "phone-runnable" means a curated form exists, not that the schema looks
        /// easy. A new add-on command therefore lands as <c>advanced</c> and stays desktop-only until
        /// someone writes it a form — the intended failure mode (§9.4).
        /// </summary>
        private static readonly HashSet<string> CuratedSimpleForms = new HashSet<string>(StringComparer.Ordinal)
        {
            "molca.build",
        };

        /// <summary>
        /// Projects the whole registered catalog, annotating each command with whether the active policy
        /// and mode would permit it right now — so the companion can grey a row and show the policy's own
        /// refusal message rather than hiding the command and inviting "why can't I run this?".
        /// </summary>
        /// <param name="kernel">The kernel to read; null yields an empty catalog.</param>
        /// <returns>The bounded catalog response object.</returns>
        internal static JObject Catalog(MolcaAutomationKernel kernel)
        {
            var commands = new JArray();
            if (kernel == null)
                return new JObject
                {
                    ["profile"] = "observe",
                    ["policySource"] = string.Empty,
                    ["catalogDigest"] = Digest(commands),
                    ["commands"] = commands
                };

            var status = kernel.StatusJson();
            foreach (var command in kernel.Capabilities().Take(MaxCatalogCommands))
                commands.Add(ProjectCommand(kernel, command));

            return new JObject
            {
                ["profile"] = WireProfile(status.Value<string>("activeProfile")),
                ["policySource"] = Truncate(status.Value<string>("policySource"), 64),
                ["catalogDigest"] = Digest(commands),
                ["commands"] = commands
            };
        }

        private static JObject ProjectCommand(MolcaAutomationKernel kernel, MolcaCommandDefinition command)
        {
            // Preview is the single source of the "allowed now" verdict: it runs the same policy and mode
            // gates the real invoke would, as an unconfirmed interactive caller, so the confirmation
            // requirement surfaces instead of being assumed granted.
            var plan = kernel.PreviewPlan(command.Id, null, MolcaTransport.Remote);
            var authorization = plan["authorization"] as JObject ?? new JObject();
            var allowed = plan.Value<bool>("wouldRun");

            return new JObject
            {
                ["id"] = Truncate(command.Id, MaxCommandIdChars),
                ["displayName"] = Truncate(command.DisplayName, MaxDisplayNameChars),
                ["description"] = Truncate(command.Description, MaxDescriptionChars),
                ["category"] = Truncate(command.Category, MaxCategoryChars),
                ["mode"] = command.Mode.ToString().ToLowerInvariant(),
                ["kind"] = Wire(command.Kind.ToString()),
                ["reversibility"] = Wire(command.Reversibility.ToString()),
                ["requiresConfirmation"] = command.RequiresConfirmation,
                ["supportsCancellation"] = command.SupportsCancellation,
                ["resourceClaims"] = new JArray(
                    command.ResourceClaims.Take(MaxResourceClaims).Select(c => c.ToString())),
                ["argumentsShape"] = ArgumentsShape(command),
                ["allowedNow"] = allowed,
                ["refusalCode"] = allowed ? null : Truncate(FirstNonEmpty(
                    authorization.Value<string>("code"),
                    plan.Value<bool>("modeSatisfied") ? (string)null : "mode.not_satisfied"), MaxDiagnosticCodeChars),
                ["refusalMessage"] = allowed ? null : Truncate(
                    (plan["blockers"] as JArray)?.Select(b => (string)b).FirstOrDefault(
                        s => !string.IsNullOrEmpty(s)),
                    MaxRefusalMessageChars)
            };
        }

        /// <summary>
        /// Derives the argument tier from the command's input schema. The schema itself is never shipped —
        /// it is unbounded author-controlled JSON — so the browser gets only this three-valued verdict and
        /// renders from its own curated form table (§8.5, §9.4).
        /// </summary>
        /// <param name="command">The command to classify.</param>
        /// <returns><c>none</c>, <c>simple</c>, or <c>advanced</c>.</returns>
        internal static string ArgumentsShape(MolcaCommandDefinition command)
        {
            if (command == null) return "none";

            JObject properties;
            try
            {
                properties = JObject.Parse(command.InputSchemaJson)["properties"] as JObject;
            }
            catch (JsonException)
            {
                // An unparseable schema is read as "offer no form", never as "no arguments needed" —
                // advanced keeps it off the phone layout while the raw JSON path still works on desktop.
                return "advanced";
            }

            if (properties == null || properties.Count == 0) return "none";
            if (!CuratedSimpleForms.Contains(command.Id)) return "advanced";
            if (properties.Count > 4) return "advanced";

            foreach (var property in properties.Properties())
            {
                var type = (property.Value as JObject)?.Value<string>("type");
                if (type != "string" && type != "number" && type != "integer" && type != "boolean")
                    return "advanced";
            }
            return "simple";
        }

        /// <summary>
        /// Projects the <c>automation</c> state block: the active profile, the catalog digest so the
        /// browser can cache and the control plane can detect drift, and bounded active/recent run lists.
        /// </summary>
        /// <param name="kernel">The kernel to read; null yields an empty-but-valid block.</param>
        /// <returns>The bounded automation state block.</returns>
        internal static JObject StateBlock(MolcaAutomationKernel kernel)
        {
            if (kernel == null)
                return new JObject
                {
                    ["profile"] = "observe",
                    ["policySource"] = string.Empty,
                    ["catalogDigest"] = string.Empty,
                    ["activeRuns"] = new JArray(),
                    ["recentRuns"] = new JArray()
                };

            var status = kernel.StatusJson();
            var active = new JArray();
            foreach (var run in kernel.RunStore.ActiveRuns().Take(MaxActiveRuns))
                active.Add(ProjectActiveRun(run));

            var recent = new JArray();
            foreach (var run in kernel.RunStore.History().Where(r => r.IsTerminal).Take(MaxRecentRuns))
                recent.Add(ProjectRecentRun(kernel, run));

            return new JObject
            {
                ["profile"] = WireProfile(status.Value<string>("activeProfile")),
                ["policySource"] = Truncate(status.Value<string>("policySource"), 64),
                ["catalogDigest"] = CachedDigest(kernel),
                ["activeRuns"] = active,
                ["recentRuns"] = recent
            };
        }

        // A state snapshot carries the digest but not the catalog, and the catalog only changes when the
        // kernel rebuilds its registry or the active profile moves. Re-previewing 64 commands on every
        // coalesced snapshot would be pure waste, so the digest is memoized against those two inputs.
        private static MolcaCommandRegistry _digestRegistry;
        private static string _digestProfile;
        private static string _digestValue = string.Empty;

        private static string CachedDigest(MolcaAutomationKernel kernel)
        {
            var profile = kernel.StatusJson().Value<string>("activeProfile");
            if (!ReferenceEquals(_digestRegistry, kernel.Registry) || _digestProfile != profile)
            {
                _digestRegistry = kernel.Registry;
                _digestProfile = profile;
                _digestValue = Catalog(kernel).Value<string>("catalogDigest");
            }
            return _digestValue;
        }

        /// <summary>
        /// Projects one in-flight run. Internal rather than private so the message-omission rule for
        /// untrusted commands can be tested against a real run handle without standing up a kernel.
        /// </summary>
        /// <param name="run">The live run handle.</param>
        /// <returns>The bounded active-run object.</returns>
        internal static JObject ProjectActiveRun(MolcaRunHandle run)
        {
            var progress = run.Progress;
            var o = new JObject
            {
                ["runId"] = Truncate(run.RunId, 64),
                ["commandId"] = Truncate(run.CommandId, MaxCommandIdChars),
                ["status"] = Wire(run.Status.ToString()),
                ["transport"] = Wire(run.Transport.ToString()),
                ["startedAt"] = Iso(run.StartedAtUtc ?? run.CreatedAtUtc)
            };

            if (!progress.HasValue) return o;
            var value = progress.Value;
            if (!value.IsIndeterminate) o["progress"] = Math.Min(1f, Math.Max(0f, value.Fraction));
            if (value.StepIndex >= 0) o["stepIndex"] = value.StepIndex;
            if (value.StepCount > 0) o["stepCount"] = value.StepCount;
            if (!string.IsNullOrEmpty(value.StepName)) o["step"] = Truncate(value.StepName, MaxStepNameChars);

            // Progress messages are command-authored. Core's built-in workflows are reviewed; a
            // third-party command's is not, so its run still reports status, progress, and step — the
            // control-flow information — while its free text stays on the machine (§8.6).
            if (!string.IsNullOrEmpty(value.Message) && CoreShippedCommands.IsTrusted(run.CommandId))
                o["message"] = Truncate(value.Message, MaxProgressMessageChars);
            return o;
        }

        private static JObject ProjectRecentRun(MolcaAutomationKernel kernel, MolcaPersistedRun run)
        {
            var result = run.ResultJson;
            var duration = result != null
                ? result.Value<long>("durationMs")
                : (long)((run.CompletedAtUtc ?? run.CreatedAtUtc) - (run.StartedAtUtc ?? run.CreatedAtUtc))
                    .TotalMilliseconds;

            return new JObject
            {
                ["runId"] = Truncate(run.RunId, 64),
                ["commandId"] = Truncate(run.CommandId, MaxCommandIdChars),
                ["status"] = Wire(run.Status.ToString()),
                ["transport"] = Wire(run.Transport.ToString()),
                ["startedAt"] = Iso(run.StartedAtUtc ?? run.CreatedAtUtc),
                ["completedAt"] = Iso(run.CompletedAtUtc),
                ["durationMs"] = Math.Max(0L, Math.Min(int.MaxValue, duration)),
                ["diagnosticCount"] = Math.Min(1000, (result?["diagnostics"] as JArray)?.Count ?? 0),
                ["verified"] = (result?["verification"] as JObject)?.Value<bool>("passed") == true,
                ["revertAvailable"] = kernel.IsRevertAvailable(run.RunId)
            };
        }

        /// <summary>
        /// Bounds a <see cref="MolcaAutomationKernel.PreviewPlan"/> result for the wire. The plan is
        /// Core-authored throughout — the only free text in it is a policy or mode-gate message — so this is
        /// a length and field bound rather than a trust filter, and the caller gets the same verdict the Hub
        /// shows next to its own Run button.
        /// </summary>
        /// <param name="plan">The plan object from <c>PreviewPlan</c>.</param>
        /// <returns>The bounded plan, or a <c>found: false</c> object when the id was unknown.</returns>
        internal static JObject BoundPlan(JObject plan)
        {
            if (plan == null || plan.Value<bool>("found") != true)
                return new JObject { ["found"] = false, ["error"] = "automation.unknown_command" };

            var authorization = plan["authorization"] as JObject ?? new JObject();
            var blockers = new JArray();
            foreach (var blocker in (plan["blockers"] as JArray ?? new JArray()).Take(4))
                blockers.Add(Truncate((string)blocker, MaxRefusalMessageChars));

            return new JObject
            {
                ["found"] = true,
                ["commandId"] = Truncate(plan.Value<string>("command"), MaxCommandIdChars),
                ["displayName"] = Truncate(plan.Value<string>("displayName"), MaxDisplayNameChars),
                ["kind"] = Wire(plan.Value<string>("kind")),
                ["mode"] = (plan.Value<string>("mode") ?? "any").ToLowerInvariant(),
                ["reversibility"] = Truncate(plan.Value<string>("reversibility"), 32),
                ["retryClassification"] = Truncate(plan.Value<string>("retryClassification"), 32),
                ["retryRationale"] = Truncate(plan.Value<string>("retryRationale"), MaxRefusalMessageChars),
                ["resourceClaims"] = new JArray(
                    (plan["resourceClaims"] as JArray ?? new JArray())
                        .Take(MaxResourceClaims).Select(c => Truncate((string)c, 32))),
                ["profile"] = WireProfile(plan.Value<string>("activeProfile")),
                ["modeSatisfied"] = plan.Value<bool>("modeSatisfied"),
                ["requiresConfirmation"] = plan.Value<bool>("requiresConfirmation"),
                ["allowed"] = authorization.Value<bool>("allowed"),
                ["refusalCode"] = Truncate(authorization.Value<string>("code"), MaxDiagnosticCodeChars),
                ["refusalMessage"] = Truncate(authorization.Value<string>("message"), MaxRefusalMessageChars),
                ["wouldRun"] = plan.Value<bool>("wouldRun"),
                ["needsConfirmationToRun"] = plan.Value<bool>("needsConfirmationToRun"),
                ["blockers"] = blockers
            };
        }

        /// <summary>
        /// Projects one run's terminal detail for <c>automation.run-status</c>: timing, the verification
        /// verdict and its evidence, bounded diagnostics, revert availability, and retry classification.
        /// The result's <c>data</c> payload is deliberately absent — it is command-authored and unbounded.
        /// </summary>
        /// <param name="kernel">The kernel owning the run.</param>
        /// <param name="runId">The run to describe.</param>
        /// <returns>The bounded run-status object, or null when the run is unknown.</returns>
        internal static JObject RunStatus(MolcaAutomationKernel kernel, string runId)
        {
            if (kernel == null || string.IsNullOrEmpty(runId)) return null;

            var record = kernel.TryGetRun(runId, out var handle)
                ? MolcaPersistedRun.FromHandle(handle)
                : kernel.RunStore.History().FirstOrDefault(r => r.RunId == runId);
            if (record == null) return null;

            var result = record.ResultJson;
            var verification = result?["verification"] as JObject;
            var evidence = (verification?["evidence"] as JArray)?
                .Select(e => (string)e)
                .Where(e => !string.IsNullOrEmpty(e));

            var diagnostics = new JArray();
            foreach (var diagnostic in (result?["diagnostics"] as JArray ?? new JArray()).Take(MaxDiagnostics))
                diagnostics.Add(new JObject
                {
                    ["code"] = Truncate(diagnostic.Value<string>("code"), MaxDiagnosticCodeChars),
                    ["message"] = Truncate(diagnostic.Value<string>("message"), MaxDiagnosticMessageChars)
                });

            return new JObject
            {
                ["runId"] = Truncate(record.RunId, 64),
                ["commandId"] = Truncate(record.CommandId, MaxCommandIdChars),
                ["status"] = Wire(record.Status.ToString()),
                ["startedAt"] = Iso(record.StartedAtUtc ?? record.CreatedAtUtc),
                ["completedAt"] = Iso(record.CompletedAtUtc),
                ["durationMs"] = Math.Max(0L, result?.Value<long>("durationMs") ?? 0L),
                ["verified"] = verification?.Value<bool>("passed") == true,
                ["verificationEvidence"] = Truncate(
                    evidence == null ? null : string.Join("; ", evidence), MaxEvidenceChars),
                ["diagnostics"] = diagnostics,
                ["revertAvailable"] = kernel.IsRevertAvailable(record.RunId),
                ["retryClassification"] = RetryClassification(kernel, record.CommandId)
            };
        }

        private static string RetryClassification(MolcaAutomationKernel kernel, string commandId) =>
            kernel.TryGetCommand(commandId, out var command)
                ? Wire(MolcaRetryPolicy.Classify(command).ToString())
                : "unknown";

        /// <summary>
        /// SHA-256 hex of the compact catalog projection, so the browser can cache a catalog and the
        /// control plane can refuse a request authorized against a stale one.
        /// </summary>
        /// <param name="commands">The projected command array.</param>
        /// <returns>Lowercase hex digest.</returns>
        private static string Digest(JArray commands)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(commands.ToString(Formatting.None)));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // Profiles share the result envelope's lower_snake_case vocabulary (UnattendedCi → unattended_ci);
        // an unrecognized profile reads as the most restrictive one rather than as "no restriction".
        private static string WireProfile(string profile) =>
            Enum.TryParse<MolcaAutomationProfile>(profile, out var parsed)
                ? Wire(parsed.ToString())
                : "observe";

        private static string Wire(string pascalName) => MolcaCommandResult.WireStatusName(pascalName);

        private static string Iso(DateTime? value) =>
            value?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

        private static string FirstNonEmpty(params string[] values) =>
            values?.FirstOrDefault(v => !string.IsNullOrEmpty(v));

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
