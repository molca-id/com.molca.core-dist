using System.Collections.Generic;
using UnityEngine;
using Molca.Networking.Configuration;

namespace Molca.Editor.Networking.Validation
{
    /// <summary>How serious a validation finding is.</summary>
    public enum NetworkValidationSeverity
    {
        /// <summary>Worth knowing; never blocks anything.</summary>
        Info = 0,

        /// <summary>Likely a mistake, or a deliberate looseness worth surfacing. Does not fail a build.</summary>
        Warning = 1,

        /// <summary>The catalog cannot be used as authored. Fails a build when the catalog opts in.</summary>
        Error = 2
    }

    /// <summary>Which kind of catalog entity a finding is attached to.</summary>
    public enum NetworkValidationEntityKind
    {
        /// <summary>The catalog asset itself.</summary>
        Catalog = 0,

        /// <summary>An environment profile.</summary>
        Environment,

        /// <summary>A service definition.</summary>
        Service,

        /// <summary>One environment binding on a service.</summary>
        Binding,

        /// <summary>A policy profile.</summary>
        PolicyProfile,

        /// <summary>A credential profile.</summary>
        CredentialProfile,

        /// <summary>An endpoint collection asset.</summary>
        EndpointCollection,

        /// <summary>An endpoint template.</summary>
        Endpoint
    }

    /// <summary>
    /// One problem found in a catalog, addressed to a specific entity so the Hub can navigate to it.
    /// </summary>
    /// <remarks>
    /// Immutable. <see cref="Code"/> is the stable machine-readable identity — Doctor findings, MCP
    /// results, and tests match on it, so codes are treated as API and are not renamed. Messages are
    /// for humans and may be reworded freely.
    /// </remarks>
    public sealed class NetworkValidationFinding
    {
        /// <summary>How serious this finding is.</summary>
        public NetworkValidationSeverity Severity { get; }

        /// <summary>Stable machine-readable code, e.g. <c>network.service.binding-missing</c>.</summary>
        public string Code { get; }

        /// <summary>Shared failure category, so runtime and authoring describe the problem the same way.</summary>
        public NetworkErrorCategory Category { get; }

        /// <summary>Which kind of entity the finding is attached to.</summary>
        public NetworkValidationEntityKind EntityKind { get; }

        /// <summary>ID of the entity, or empty when the entity has no usable ID (which is often the finding).</summary>
        public string EntityId { get; }

        /// <summary>Environment the finding is scoped to, or empty when it is environment-independent.</summary>
        public string EnvironmentId { get; }

        /// <summary>Human-readable description of what is wrong.</summary>
        public string Message { get; }

        /// <summary>What the author should do, or empty when the message is self-explanatory.</summary>
        public string Remedy { get; }

        /// <summary>
        /// Asset to select when the user chooses <b>Open</b> — the catalog or an endpoint collection.
        /// May be <c>null</c>.
        /// </summary>
        public Object TargetObject { get; }

        /// <summary>Creates a finding.</summary>
        /// <param name="severity">How serious it is.</param>
        /// <param name="code">Stable machine-readable code.</param>
        /// <param name="category">Shared failure category.</param>
        /// <param name="entityKind">Kind of entity the finding attaches to.</param>
        /// <param name="entityId">Entity ID, or <c>null</c>.</param>
        /// <param name="message">Human-readable description.</param>
        /// <param name="remedy">Suggested fix, or <c>null</c>.</param>
        /// <param name="environmentId">Scoping environment, or <c>null</c>.</param>
        /// <param name="targetObject">Asset to select on Open, or <c>null</c>.</param>
        public NetworkValidationFinding(
            NetworkValidationSeverity severity,
            string code,
            NetworkErrorCategory category,
            NetworkValidationEntityKind entityKind,
            string entityId,
            string message,
            string remedy = null,
            string environmentId = null,
            Object targetObject = null)
        {
            Severity = severity;
            Code = code;
            Category = category;
            EntityKind = entityKind;
            EntityId = entityId ?? string.Empty;
            Message = message;
            Remedy = remedy ?? string.Empty;
            EnvironmentId = environmentId ?? string.Empty;
            TargetObject = targetObject;
        }

        /// <summary>Renders the finding for logs and test failure messages.</summary>
        public override string ToString()
        {
            string scope = string.IsNullOrEmpty(EnvironmentId)
                ? EntityId
                : $"{EntityId}@{EnvironmentId}";

            return string.IsNullOrEmpty(scope)
                ? $"[{Severity}] {Code}: {Message}"
                : $"[{Severity}] {Code} ({scope}): {Message}";
        }
    }

    /// <summary>
    /// The result of validating one catalog: every finding, plus the counts callers gate on.
    /// </summary>
    /// <remarks>
    /// Deterministic — findings come out in a fixed order for a given catalog, so batch-mode runs and
    /// golden-file tests are stable.
    /// </remarks>
    public sealed class NetworkValidationReport
    {
        private readonly List<NetworkValidationFinding> _findings;

        /// <summary>Every finding, in deterministic order.</summary>
        public IReadOnlyList<NetworkValidationFinding> Findings => _findings;

        /// <summary>The catalog that was validated. May be <c>null</c> when no catalog was found.</summary>
        public NetworkCatalog Catalog { get; }

        /// <summary>Number of <see cref="NetworkValidationSeverity.Error"/> findings.</summary>
        public int ErrorCount { get; }

        /// <summary>Number of <see cref="NetworkValidationSeverity.Warning"/> findings.</summary>
        public int WarningCount { get; }

        /// <summary>Number of <see cref="NetworkValidationSeverity.Info"/> findings.</summary>
        public int InfoCount { get; }

        /// <summary>Whether the catalog is free of errors and therefore usable.</summary>
        public bool IsValid => ErrorCount == 0;

        /// <summary>Creates a report.</summary>
        /// <param name="catalog">The validated catalog, or <c>null</c>.</param>
        /// <param name="findings">The findings; copied defensively.</param>
        public NetworkValidationReport(NetworkCatalog catalog, IEnumerable<NetworkValidationFinding> findings)
        {
            Catalog = catalog;
            _findings = findings == null
                ? new List<NetworkValidationFinding>()
                : new List<NetworkValidationFinding>(findings);

            foreach (var finding in _findings)
            {
                switch (finding.Severity)
                {
                    case NetworkValidationSeverity.Error: ErrorCount++; break;
                    case NetworkValidationSeverity.Warning: WarningCount++; break;
                    default: InfoCount++; break;
                }
            }
        }

        /// <summary>Whether a finding with the given code is present.</summary>
        /// <param name="code">The stable finding code to look for.</param>
        public bool HasCode(string code)
        {
            foreach (var finding in _findings)
            {
                if (finding.Code == code)
                    return true;
            }
            return false;
        }

        /// <summary>Findings at or above a severity, in report order.</summary>
        /// <param name="minimum">Lowest severity to include.</param>
        public List<NetworkValidationFinding> AtLeast(NetworkValidationSeverity minimum)
        {
            var result = new List<NetworkValidationFinding>();
            foreach (var finding in _findings)
            {
                if (finding.Severity >= minimum)
                    result.Add(finding);
            }
            return result;
        }

        /// <summary>A one-line summary, e.g. <c>2 errors, 1 warning, 0 info</c>.</summary>
        public string Summarize() =>
            $"{ErrorCount} error{(ErrorCount == 1 ? "" : "s")}, " +
            $"{WarningCount} warning{(WarningCount == 1 ? "" : "s")}, " +
            $"{InfoCount} info";
    }
}
