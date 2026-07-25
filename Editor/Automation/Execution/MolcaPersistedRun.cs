using System;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// A compact, serializable snapshot of one run for the on-disk history under
    /// <c>Library/Molca/Automation/Runs/</c> (§12): identity, transport, status, timings, and the
    /// run's result envelope. Persisted by <see cref="MolcaRunJournal"/> so history and interrupted-run
    /// recovery survive a domain reload — the in-memory <see cref="MolcaRunStore"/> does not.
    /// </summary>
    public sealed class MolcaPersistedRun
    {
        /// <summary>Unique run id.</summary>
        public string RunId { get; }

        /// <summary>The command this run executed.</summary>
        public string CommandId { get; }

        /// <summary>The transport that started the run.</summary>
        public MolcaTransport Transport { get; }

        /// <summary>The run's status at the time it was persisted.</summary>
        public MolcaCommandStatus Status { get; }

        /// <summary>UTC creation time.</summary>
        public DateTime CreatedAtUtc { get; }

        /// <summary>UTC start time, or null if it never left the queue.</summary>
        public DateTime? StartedAtUtc { get; }

        /// <summary>UTC completion time, or null if it was still running when persisted.</summary>
        public DateTime? CompletedAtUtc { get; }

        /// <summary>The compact result envelope, or null while the run was still in flight.</summary>
        public JObject ResultJson { get; }

        /// <summary>Sort key: completion time when terminal, else creation time.</summary>
        public DateTime OrderingTimeUtc => CompletedAtUtc ?? StartedAtUtc ?? CreatedAtUtc;

        /// <summary>Whether the persisted status is a terminal (non-in-flight) one.</summary>
        public bool IsTerminal =>
            Status != MolcaCommandStatus.Queued && Status != MolcaCommandStatus.Running &&
            Status != MolcaCommandStatus.NeedsConfirmation;

        /// <summary>Creates a persisted-run record.</summary>
        /// <param name="runId">Run id.</param>
        /// <param name="commandId">Command id.</param>
        /// <param name="transport">Originating transport.</param>
        /// <param name="status">Run status.</param>
        /// <param name="createdAtUtc">Creation time.</param>
        /// <param name="startedAtUtc">Start time, or null.</param>
        /// <param name="completedAtUtc">Completion time, or null.</param>
        /// <param name="resultJson">Compact result envelope, or null.</param>
        public MolcaPersistedRun(
            string runId, string commandId, MolcaTransport transport, MolcaCommandStatus status,
            DateTime createdAtUtc, DateTime? startedAtUtc, DateTime? completedAtUtc, JObject resultJson)
        {
            RunId = runId;
            CommandId = commandId;
            Transport = transport;
            Status = status;
            CreatedAtUtc = createdAtUtc;
            StartedAtUtc = startedAtUtc;
            CompletedAtUtc = completedAtUtc;
            ResultJson = resultJson;
        }

        /// <summary>Projects a live run handle to a persisted record.</summary>
        /// <param name="handle">The run handle.</param>
        /// <returns>A persisted-run snapshot.</returns>
        public static MolcaPersistedRun FromHandle(MolcaRunHandle handle) => new MolcaPersistedRun(
            handle.RunId, handle.CommandId, handle.Transport, handle.Status,
            handle.CreatedAtUtc, handle.StartedAtUtc, handle.CompletedAtUtc,
            handle.Result != null ? handle.Result.ToJson() : null);

        /// <summary>Returns a copy with a different status (used when reconciling an interrupted run).</summary>
        /// <param name="status">The reconciled status.</param>
        /// <returns>A new record with the given status.</returns>
        public MolcaPersistedRun WithStatus(MolcaCommandStatus status) => new MolcaPersistedRun(
            RunId, CommandId, Transport, status, CreatedAtUtc, StartedAtUtc,
            status == MolcaCommandStatus.Interrupted ? (CompletedAtUtc ?? DateTime.UtcNow) : CompletedAtUtc,
            ResultJson);

        /// <summary>Serializes this record to its on-disk JSON form.</summary>
        /// <returns>A <see cref="JObject"/> capturing the whole record.</returns>
        public JObject ToJson() => new JObject
        {
            ["runId"] = RunId,
            ["command"] = CommandId,
            ["transport"] = Transport.ToString(),
            ["status"] = MolcaCommandResult.WireStatusName(Status.ToString()),
            ["createdAtUtc"] = CreatedAtUtc.ToString("o"),
            ["startedAtUtc"] = StartedAtUtc?.ToString("o"),
            ["completedAtUtc"] = CompletedAtUtc?.ToString("o"),
            ["result"] = ResultJson
        };

        /// <summary>Parses a record from its on-disk JSON form.</summary>
        /// <param name="json">The persisted JSON.</param>
        /// <returns>The record, or null if the JSON is missing a run id.</returns>
        public static MolcaPersistedRun FromJson(JObject json)
        {
            if (json == null) return null;
            var runId = json.Value<string>("runId");
            if (string.IsNullOrEmpty(runId)) return null;

            Enum.TryParse(WireToPascal(json.Value<string>("status")), out MolcaCommandStatus status);
            Enum.TryParse(json.Value<string>("transport"), out MolcaTransport transport);

            return new MolcaPersistedRun(
                runId,
                json.Value<string>("command"),
                transport,
                status,
                ParseUtc(json.Value<string>("createdAtUtc")) ?? DateTime.UtcNow,
                ParseUtc(json.Value<string>("startedAtUtc")),
                ParseUtc(json.Value<string>("completedAtUtc")),
                json["result"] as JObject);
        }

        private static DateTime? ParseUtc(string s) =>
            DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt) ? dt : (DateTime?)null;

        // Reverse of WireStatusName for the small closed status vocabulary (e.g. needs_confirmation → NeedsConfirmation).
        private static string WireToPascal(string wire)
        {
            if (string.IsNullOrEmpty(wire)) return wire;
            var parts = wire.Split('_');
            var sb = new System.Text.StringBuilder(wire.Length);
            foreach (var p in parts)
                if (p.Length > 0) sb.Append(char.ToUpperInvariant(p[0])).Append(p.Substring(1));
            return sb.ToString();
        }
    }
}
