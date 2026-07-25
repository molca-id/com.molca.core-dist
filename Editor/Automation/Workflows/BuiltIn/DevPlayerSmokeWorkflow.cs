using System;
using System.Linq;
using Molca.DevPlayer;
using Molca.Editor.Automation.DevPlayer;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Automation.BuiltIn
{
    /// <summary>
    /// The Development Player smoke workflow (§17 Phase 5): connects to a running development build over
    /// <c>EditorConnection</c> and pulls a read-only <see cref="MolcaDevPlayerSnapshot"/> — RuntimeManager
    /// state, subsystem health, Sequence controllers, and a recent-error tally — so a QA/dev build can be
    /// observed and smoke-tested locally. Read-only: it only asks the Player to describe itself.
    /// </summary>
    /// <remarks>
    /// Requires a connected development Player (the bridge exists only in development builds and the
    /// Editor's Play mode). With none connected, the connection step fails with a clear diagnostic rather
    /// than an error. The probe is bounded by a <c>timeoutMs</c> argument (default 3000) so a silent
    /// Player never hangs the run.
    /// </remarks>
    public static class DevPlayerSmokeWorkflow
    {
        /// <summary>The stable command id of the Development Player smoke workflow.</summary>
        public const string Id = "molca.dev-player-smoke";

        private const int DefaultTimeoutMs = 3000;

        /// <summary>Builds the Development Player smoke workflow definition.</summary>
        /// <returns>The workflow definition.</returns>
        public static MolcaWorkflowDefinition Create() => new MolcaWorkflowDefinition(
            id: Id,
            displayName: "Dev Player Smoke",
            description: "Read-only smoke of a connected development build: RuntimeManager state, subsystem health, and error tally over PlayerConnection.",
            steps: new[]
            {
                new MolcaWorkflowStep("connection", "Confirm a development player is connected.", ConnectionStep),
                new MolcaWorkflowStep("probe", "Request and assess a read-only diagnostics snapshot from the player.", ProbeStep),
            },
            mode: MolcaCommandMode.Any,
            kind: MolcaCommandKind.ReadOnly,
            resourceClaims: new[] { MolcaResourceClaim.DevelopmentPlayer });

        private static Awaitable<MolcaStepResult> ConnectionStep(MolcaCommandContext context)
        {
            var players = MolcaDevPlayerProbe.ConnectedPlayers();
            var data = new JObject
            {
                ["connectedPlayerCount"] = players.Count,
                ["players"] = new JArray(players)
            };
            return Completed(players.Count == 0
                ? MolcaStepResult.Fail(new[] { new MolcaDiagnostic("dev_player.none",
                    "No development player is connected — start a development build (or enter Play mode) and connect it to this Editor.") }, data)
                : MolcaStepResult.Pass(data));
        }

        private static async Awaitable<MolcaStepResult> ProbeStep(MolcaCommandContext context)
        {
            int timeoutMs = context.Arguments["timeoutMs"]?.Value<int?>() ?? DefaultTimeoutMs;

            string json;
            try
            {
                json = await MolcaDevPlayerProbe.RequestSnapshotJsonAsync(timeoutMs, context.CancellationToken);
            }
            catch (TimeoutException ex)
            {
                return MolcaStepResult.Fail("dev_player.timeout", ex.Message);
            }
            catch (OperationCanceledException)
            {
                return MolcaStepResult.Fail("dev_player.cancelled", "The probe was cancelled.");
            }

            MolcaDevPlayerSnapshot snapshot;
            try { snapshot = JsonUtility.FromJson<MolcaDevPlayerSnapshot>(json); }
            catch (Exception ex)
            {
                return MolcaStepResult.Fail("dev_player.bad_snapshot", $"Could not parse the player's snapshot: {ex.Message}");
            }

            return AssessSnapshot(snapshot);
        }

        /// <summary>
        /// Turns a player snapshot into a pass/fail step result. A player that never reached Ready or has
        /// no resolved subsystems fails; inactive subsystems and logged errors are surfaced as warnings.
        /// Pure so it can be unit-tested without a live connection.
        /// </summary>
        /// <param name="snapshot">The snapshot returned by the player.</param>
        /// <returns>The assessed step result.</returns>
        public static MolcaStepResult AssessSnapshot(MolcaDevPlayerSnapshot snapshot)
        {
            var diagnostics = new System.Collections.Generic.List<MolcaDiagnostic>();

            foreach (var name in snapshot.inactiveSubsystems ?? Array.Empty<string>())
                diagnostics.Add(new MolcaDiagnostic("dev_player.subsystem_inactive",
                    $"Subsystem '{name}' resolved but is not active.", MolcaDiagnosticSeverity.Warning));

            if (snapshot.recentErrorCount > 0)
                diagnostics.Add(new MolcaDiagnostic("dev_player.errors_logged",
                    $"The player logged {snapshot.recentErrorCount} error(s)/exception(s).", MolcaDiagnosticSeverity.Warning));

            var data = new JObject
            {
                ["bootstrapState"] = snapshot.bootstrapState,
                ["isReady"] = snapshot.isReady,
                ["subsystemResolvedCount"] = snapshot.subsystemResolvedCount,
                ["subsystemDiscoveredCount"] = snapshot.subsystemDiscoveredCount,
                ["inactiveSubsystems"] = new JArray(snapshot.inactiveSubsystems ?? Array.Empty<string>()),
                ["recentErrorCount"] = snapshot.recentErrorCount,
                ["isDevelopmentBuild"] = snapshot.isDevelopmentBuild,
                ["platform"] = snapshot.platform,
                ["unityVersion"] = snapshot.unityVersion,
                ["productName"] = snapshot.productName
            };

            if (!snapshot.isReady)
            {
                diagnostics.Insert(0, new MolcaDiagnostic("dev_player.not_ready",
                    $"The player's RuntimeManager did not reach Ready (state: {snapshot.bootstrapState})."));
                return MolcaStepResult.Fail(diagnostics, data);
            }
            if (snapshot.subsystemResolvedCount == 0)
            {
                diagnostics.Insert(0, new MolcaDiagnostic("dev_player.no_subsystems",
                    "The player is Ready but no subsystems resolved."));
                return MolcaStepResult.Fail(diagnostics, data);
            }

            return MolcaStepResult.Pass(data, diagnostics);
        }

        private static Awaitable<MolcaStepResult> Completed(MolcaStepResult result)
        {
            var source = new AwaitableCompletionSource<MolcaStepResult>();
            source.SetResult(result);
            return source.Awaitable;
        }
    }
}
