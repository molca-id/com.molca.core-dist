using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// Severity of a <see cref="MolcaDiagnostic"/>. Ordered so callers can filter at or above a level.
    /// </summary>
    public enum MolcaDiagnosticSeverity
    {
        /// <summary>Informational; not a problem.</summary>
        Info,

        /// <summary>A concern that did not fail the command.</summary>
        Warning,

        /// <summary>A failure condition.</summary>
        Error
    }

    /// <summary>
    /// One structured diagnostic on a <see cref="MolcaCommandResult"/>. Every diagnostic carries a
    /// stable <see cref="Code"/> in addition to a human <see cref="Message"/> so automation branches on
    /// codes, never on matching log strings (§8). Optional <see cref="Path"/>/<see cref="Line"/> point
    /// at a source location where meaningful.
    /// </summary>
    public sealed class MolcaDiagnostic
    {
        /// <summary>Stable machine-readable code (e.g. <c>mode.play_required</c>, <c>policy.refused</c>).</summary>
        public string Code { get; }

        /// <summary>Human/LLM-facing message.</summary>
        public string Message { get; }

        /// <summary>Severity of this diagnostic.</summary>
        public MolcaDiagnosticSeverity Severity { get; }

        /// <summary>Optional source path this diagnostic refers to, or null.</summary>
        public string Path { get; }

        /// <summary>Optional 1-based line within <see cref="Path"/>, or 0 when not applicable.</summary>
        public int Line { get; }

        /// <summary>Creates a structured diagnostic.</summary>
        /// <param name="code">Stable machine-readable code.</param>
        /// <param name="message">Human-facing message.</param>
        /// <param name="severity">Severity; defaults to <see cref="MolcaDiagnosticSeverity.Error"/>.</param>
        /// <param name="path">Optional source path.</param>
        /// <param name="line">Optional 1-based line.</param>
        public MolcaDiagnostic(string code, string message,
            MolcaDiagnosticSeverity severity = MolcaDiagnosticSeverity.Error, string path = null, int line = 0)
        {
            Code = string.IsNullOrWhiteSpace(code) ? "unknown" : code;
            Message = message ?? string.Empty;
            Severity = severity;
            Path = path;
            Line = line;
        }

        /// <summary>Serializes this diagnostic to its JSON object form.</summary>
        /// <returns>A <see cref="JObject"/> with code/severity/message and optional path/line.</returns>
        public JObject ToJson()
        {
            var o = new JObject
            {
                ["code"] = Code,
                ["severity"] = Severity.ToString(),
                ["message"] = Message
            };
            if (!string.IsNullOrEmpty(Path)) o["path"] = Path;
            if (Line > 0) o["line"] = Line;
            return o;
        }
    }

    /// <summary>
    /// A file or data artifact a command produced (e.g. a build output, an evidence bundle). Paths are
    /// contained to the project or an approved artifact directory (§15); the kernel never returns
    /// arbitrary file contents inline.
    /// </summary>
    public sealed class MolcaArtifact
    {
        /// <summary>Stable kind label (e.g. <c>build-output</c>, <c>evidence-bundle</c>).</summary>
        public string Kind { get; }

        /// <summary>Project- or artifact-root-relative path to the artifact.</summary>
        public string Path { get; }

        /// <summary>Optional SHA-256 (hex) of the artifact bytes, or null.</summary>
        public string Sha256 { get; }

        /// <summary>Optional size in bytes, or null when unknown.</summary>
        public long? SizeBytes { get; }

        /// <summary>Creates an artifact descriptor.</summary>
        /// <param name="kind">Stable kind label.</param>
        /// <param name="path">Contained relative path.</param>
        /// <param name="sha256">Optional SHA-256 hex.</param>
        /// <param name="sizeBytes">Optional byte size.</param>
        public MolcaArtifact(string kind, string path, string sha256 = null, long? sizeBytes = null)
        {
            Kind = kind ?? string.Empty;
            Path = path ?? string.Empty;
            Sha256 = sha256;
            SizeBytes = sizeBytes;
        }

        /// <summary>Serializes this artifact to its JSON object form.</summary>
        /// <returns>A <see cref="JObject"/> describing the artifact.</returns>
        public JObject ToJson()
        {
            var o = new JObject { ["kind"] = Kind, ["path"] = Path };
            if (!string.IsNullOrEmpty(Sha256)) o["sha256"] = Sha256;
            if (SizeBytes.HasValue) o["sizeBytes"] = SizeBytes.Value;
            return o;
        }
    }

    /// <summary>
    /// Outcome of a command's optional post-execution verification (§8, §13). A command whose delegate
    /// returned successfully still <em>fails</em> when <see cref="Passed"/> is false — verification is
    /// the real postcondition, not the delegate's return.
    /// </summary>
    public sealed class MolcaVerification
    {
        /// <summary>Whether any verification ran.</summary>
        public bool Performed { get; }

        /// <summary>Whether the postcondition held. Meaningless when <see cref="Performed"/> is false.</summary>
        public bool Passed { get; }

        /// <summary>Human-readable evidence lines supporting the verdict.</summary>
        public IReadOnlyList<string> Evidence { get; }

        /// <summary>The "no verification performed" singleton.</summary>
        public static readonly MolcaVerification NotPerformed = new MolcaVerification(false, false, null);

        /// <summary>Creates a verification outcome.</summary>
        /// <param name="performed">Whether verification ran.</param>
        /// <param name="passed">Whether the postcondition held.</param>
        /// <param name="evidence">Optional evidence lines.</param>
        public MolcaVerification(bool performed, bool passed, IReadOnlyList<string> evidence)
        {
            Performed = performed;
            Passed = passed;
            Evidence = evidence ?? Array.Empty<string>();
        }

        /// <summary>Serializes this verification to its JSON object form.</summary>
        /// <returns>A <see cref="JObject"/> with performed/passed/evidence.</returns>
        public JObject ToJson() => new JObject
        {
            ["performed"] = Performed,
            ["passed"] = Passed,
            ["evidence"] = new JArray(Evidence)
        };
    }

    /// <summary>
    /// The revert path available for a completed action (§8). <see cref="Kind"/> mirrors
    /// <see cref="MolcaCommandReversibility"/>; <see cref="Id"/> is the handle a future rollback call
    /// uses (e.g. a file-snapshot id), or null when none.
    /// </summary>
    public sealed class MolcaRevertInfo
    {
        /// <summary>The reversibility kind of the completed action.</summary>
        public MolcaCommandReversibility Kind { get; }

        /// <summary>Opaque handle used to perform the revert, or null.</summary>
        public string Id { get; }

        /// <summary>The "no revert available" singleton.</summary>
        public static readonly MolcaRevertInfo None = new MolcaRevertInfo(MolcaCommandReversibility.None, null);

        /// <summary>Creates a revert descriptor.</summary>
        /// <param name="kind">Reversibility kind.</param>
        /// <param name="id">Opaque revert handle, or null.</param>
        public MolcaRevertInfo(MolcaCommandReversibility kind, string id)
        {
            Kind = kind;
            Id = id;
        }

        /// <summary>Serializes this revert descriptor to its JSON object form.</summary>
        /// <returns>A <see cref="JObject"/> with kind/id (kind lowercased to the wire vocabulary).</returns>
        public JObject ToJson() => new JObject
        {
            ["kind"] = MolcaCommandResult.WireStatusName(Kind.ToString()),
            ["id"] = Id
        };
    }

    /// <summary>
    /// The single, versioned, machine-readable result of one command or workflow run. Serializes to the
    /// stable envelope in §8. Every transport (CLI/Pipeline, Hub, MCP, Assistant, batch) returns this
    /// shape, so no caller ever scrapes Editor logs to determine success.
    /// </summary>
    /// <remarks>
    /// Construct terminal results with the <see cref="Succeeded"/>, <see cref="Failed"/>,
    /// <see cref="Refused"/>, <see cref="Blocked"/>, or <see cref="Cancelled"/> factories; the coordinator
    /// stamps <see cref="StartedAtUtc"/>/<see cref="DurationMs"/>/<see cref="RunId"/> as a run progresses.
    /// Immutable once constructed.
    /// </remarks>
    public sealed class MolcaCommandResult
    {
        /// <summary>Current output schema version of this envelope (§8).</summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>The output schema version of this result.</summary>
        public int SchemaVersion { get; }

        /// <summary>The run id this result belongs to (uuid), or null before assignment.</summary>
        public string RunId { get; internal set; }

        /// <summary>The stable command id this result is for.</summary>
        public string Command { get; }

        /// <summary>Terminal or in-flight status.</summary>
        public MolcaCommandStatus Status { get; }

        /// <summary>Convenience flag; true only when <see cref="Status"/> is <see cref="MolcaCommandStatus.Succeeded"/>.</summary>
        public bool Success => Status == MolcaCommandStatus.Succeeded;

        /// <summary>UTC start timestamp, or null before the run started.</summary>
        public DateTime? StartedAtUtc { get; internal set; }

        /// <summary>Wall-clock duration in milliseconds, or 0 before completion.</summary>
        public long DurationMs { get; internal set; }

        /// <summary>Command-specific payload (the equivalent of the MCP tool's JSON result). Never null.</summary>
        public JToken Data { get; }

        /// <summary>Structured diagnostics (with stable codes). Never null.</summary>
        public IReadOnlyList<MolcaDiagnostic> Diagnostics { get; }

        /// <summary>Declared artifacts produced by the run. Never null.</summary>
        public IReadOnlyList<MolcaArtifact> Artifacts { get; }

        /// <summary>Post-execution verification outcome. Never null.</summary>
        public MolcaVerification Verification { get; }

        /// <summary>Available revert path. Never null.</summary>
        public MolcaRevertInfo Revert { get; }

        private MolcaCommandResult(
            string command, MolcaCommandStatus status, JToken data,
            IReadOnlyList<MolcaDiagnostic> diagnostics, IReadOnlyList<MolcaArtifact> artifacts,
            MolcaVerification verification, MolcaRevertInfo revert)
        {
            SchemaVersion = CurrentSchemaVersion;
            Command = command ?? string.Empty;
            Status = status;
            Data = data ?? new JObject();
            Diagnostics = diagnostics ?? Array.Empty<MolcaDiagnostic>();
            Artifacts = artifacts ?? Array.Empty<MolcaArtifact>();
            Verification = verification ?? MolcaVerification.NotPerformed;
            Revert = revert ?? MolcaRevertInfo.None;
        }

        /// <summary>Creates a fully-specified result (used by the executor once a run completes).</summary>
        /// <param name="command">Stable command id.</param>
        /// <param name="status">Terminal status.</param>
        /// <param name="data">Command payload (may be null → empty object).</param>
        /// <param name="diagnostics">Structured diagnostics.</param>
        /// <param name="artifacts">Declared artifacts.</param>
        /// <param name="verification">Verification outcome.</param>
        /// <param name="revert">Revert path.</param>
        /// <returns>A new immutable result.</returns>
        public static MolcaCommandResult Create(
            string command, MolcaCommandStatus status, JToken data = null,
            IReadOnlyList<MolcaDiagnostic> diagnostics = null, IReadOnlyList<MolcaArtifact> artifacts = null,
            MolcaVerification verification = null, MolcaRevertInfo revert = null)
            => new MolcaCommandResult(command, status, data, diagnostics, artifacts, verification, revert);

        /// <summary>A succeeded result carrying <paramref name="data"/>.</summary>
        /// <param name="command">Stable command id.</param>
        /// <param name="data">Command payload.</param>
        /// <param name="verification">Optional verification outcome.</param>
        /// <param name="revert">Optional revert path.</param>
        /// <returns>A succeeded result.</returns>
        public static MolcaCommandResult Succeeded(string command, JToken data = null,
            MolcaVerification verification = null, MolcaRevertInfo revert = null)
            => new MolcaCommandResult(command, MolcaCommandStatus.Succeeded, data, null, null, verification, revert);

        /// <summary>A failed result carrying one or more diagnostics.</summary>
        /// <param name="command">Stable command id.</param>
        /// <param name="diagnostics">Failure diagnostics.</param>
        /// <param name="data">Optional partial payload.</param>
        /// <returns>A failed result.</returns>
        public static MolcaCommandResult Failed(string command, IReadOnlyList<MolcaDiagnostic> diagnostics, JToken data = null)
            => new MolcaCommandResult(command, MolcaCommandStatus.Failed, data, diagnostics, null, null, null);

        /// <summary>A failed result from a single code/message.</summary>
        /// <param name="command">Stable command id.</param>
        /// <param name="code">Stable diagnostic code.</param>
        /// <param name="message">Human message.</param>
        /// <returns>A failed result.</returns>
        public static MolcaCommandResult Fail(string command, string code, string message)
            => Failed(command, new[] { new MolcaDiagnostic(code, message) });

        /// <summary>A policy/mode refusal (an authorization outcome, not an error).</summary>
        /// <param name="command">Stable command id.</param>
        /// <param name="code">Stable diagnostic code.</param>
        /// <param name="message">Human message.</param>
        /// <returns>A refused result.</returns>
        public static MolcaCommandResult Refused(string command, string code, string message)
            => new MolcaCommandResult(command, MolcaCommandStatus.Refused, null,
                new[] { new MolcaDiagnostic(code, message, MolcaDiagnosticSeverity.Warning) }, null, null, null);

        /// <summary>A "could not acquire resources" outcome; the caller may queue or retry.</summary>
        /// <param name="command">Stable command id.</param>
        /// <param name="message">Human message describing the contended resource.</param>
        /// <returns>A blocked result.</returns>
        public static MolcaCommandResult Blocked(string command, string message)
            => new MolcaCommandResult(command, MolcaCommandStatus.Blocked, null,
                new[] { new MolcaDiagnostic("resource.blocked", message, MolcaDiagnosticSeverity.Warning) }, null, null, null);

        /// <summary>A cancelled outcome (caller or lifetime token).</summary>
        /// <param name="command">Stable command id.</param>
        /// <returns>A cancelled result.</returns>
        public static MolcaCommandResult Cancelled(string command)
            => new MolcaCommandResult(command, MolcaCommandStatus.Cancelled, null,
                new[] { new MolcaDiagnostic("run.cancelled", "The run was cancelled.", MolcaDiagnosticSeverity.Info) },
                null, null, null);

        /// <summary>Serializes to the stable §8 envelope.</summary>
        /// <returns>A <see cref="JObject"/> matching the documented result schema.</returns>
        public JObject ToJson() => new JObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["runId"] = RunId,
            ["command"] = Command,
            ["status"] = WireStatusName(Status.ToString()),
            ["success"] = Success,
            ["startedAtUtc"] = StartedAtUtc?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["durationMs"] = DurationMs,
            ["data"] = Data,
            ["diagnostics"] = new JArray(Diagnostics.Select(d => d.ToJson())),
            ["artifacts"] = new JArray(Artifacts.Select(a => a.ToJson())),
            ["verification"] = Verification.ToJson(),
            ["revert"] = Revert.ToJson()
        };

        /// <summary>Serializes to a compact JSON string (the wire form callers receive).</summary>
        /// <returns>Compact JSON.</returns>
        public string ToJsonString() => ToJson().ToString(Formatting.None);

        /// <summary>
        /// Maps a PascalCase enum name to the lower_snake_case wire vocabulary used in §8
        /// (e.g. <c>NeedsConfirmation</c> → <c>needs_confirmation</c>, <c>CompensatingAction</c> →
        /// <c>compensating_action</c>). Shared by status and revert-kind serialization.
        /// </summary>
        /// <param name="pascalName">The enum member name.</param>
        /// <returns>The lower_snake_case wire token.</returns>
        internal static string WireStatusName(string pascalName)
        {
            if (string.IsNullOrEmpty(pascalName)) return pascalName;
            var sb = new System.Text.StringBuilder(pascalName.Length + 4);
            for (int i = 0; i < pascalName.Length; i++)
            {
                var c = pascalName[i];
                if (char.IsUpper(c))
                {
                    if (i > 0) sb.Append('_');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
