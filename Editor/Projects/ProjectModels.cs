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
