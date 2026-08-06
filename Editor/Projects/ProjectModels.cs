using System;

namespace Molca.Editor.Projects
{
    [Serializable]
    internal sealed class MolcaBackendProject
    {
        public string id;
        public string code;
        public string name;
        public string status;
        public string membershipRole;
        public string createdAt;
        public string updatedAt;
    }

    [Serializable]
    internal sealed class ProjectListResponse
    {
        public string role;
        public MolcaBackendProject[] projects;
    }

    [Serializable]
    internal sealed class CreateProjectRequest
    {
        public string name;
    }

    [Serializable]
    internal sealed class ProjectBindingResponse
    {
        public MolcaBackendProject project;
        public string bindingId;
        public string projectBinding;
        public string issuedAt;
    }

    [Serializable]
    internal sealed class ProjectBindingPayload
    {
        public int schemaVersion;
        public string kind;
        public string bindingId;
        public string projectId;
        public string projectCode;
        public string licenseeId;
        public string issuedAt;
    }

    /// <summary>
    /// One project's operational health report, as returned by
    /// <c>GET /api/projects/:projectId/health</c>.
    /// </summary>
    /// <remarks>
    /// Every judgement in here — each panel's severity, the overall severity, and the wording of every
    /// finding — is computed by the control plane, not by the Editor. That is the point: the customer
    /// dashboard, the Hub, and the operator support view render one report, so "unhealthy" cannot mean three
    /// different things depending on which client the reader opened.
    /// <para>
    /// The server payload also carries per-panel detail (token counts, active channels, recent builds).
    /// <see cref="UnityEngine.JsonUtility"/> drops fields a type does not declare, and these types
    /// deliberately declare only the report's judgement: the Hub states what is wrong and what to do, and
    /// sends the reader to the dashboard for the underlying rows.
    /// </para>
    /// </remarks>
    [Serializable]
    internal sealed class ProjectHealthResponse
    {
        /// <summary>The project this report describes.</summary>
        public string projectId;

        /// <summary>Worst severity across the panels the caller may see: ok, attention, or problem.</summary>
        public string severity;

        /// <summary>Server-authored one-line summary of the whole report.</summary>
        public string summary;

        /// <summary>When the server assembled the report, ISO-8601 UTC.</summary>
        public string generatedAt;

        /// <summary>Panels the caller is authorized for; one the caller may not see is absent, not empty.</summary>
        public ProjectHealthPanel[] panels;
    }

    /// <summary>One area of a <see cref="ProjectHealthResponse"/>: connection, content, builds, and so on.</summary>
    [Serializable]
    internal sealed class ProjectHealthPanel
    {
        /// <summary>Stable panel id (<c>connection</c>, <c>content</c>, <c>builds</c>, …).</summary>
        public string id;

        /// <summary>Display title.</summary>
        public string title;

        /// <summary>This panel's severity: ok, attention, or problem.</summary>
        public string severity;

        /// <summary>One-line state of this area.</summary>
        public string summary;

        /// <summary>What is wrong here, worst-first; empty when nothing needs attention.</summary>
        public ProjectHealthFinding[] findings;
    }

    /// <summary>One thing worth acting on, and where to act on it.</summary>
    [Serializable]
    internal sealed class ProjectHealthFinding
    {
        /// <summary>This finding's severity: ok, attention, or problem.</summary>
        public string severity;

        /// <summary>What is wrong.</summary>
        public string message;

        /// <summary>What to do about it. Never empty for a finding worse than ok.</summary>
        public string action;
    }

    [Serializable]
    internal sealed class ProjectApiError
    {
        public string reason;
    }

    internal readonly struct ProjectApiResult<T>
    {
        private ProjectApiResult(bool success, T value, string error)
        {
            Success = success;
            Value = value;
            Error = error;
        }

        public bool Success { get; }
        public T Value { get; }
        public string Error { get; }

        public static ProjectApiResult<T> Ok(T value) => new ProjectApiResult<T>(true, value, null);
        public static ProjectApiResult<T> Fail(string error) => new ProjectApiResult<T>(false, default, error);
    }
}
