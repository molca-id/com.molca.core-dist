using System;

namespace Molca.Settings.Integration.GitHub
{
    /// <summary>
    /// Serializable DTOs for the GitHub REST API, shaped for Unity's <see cref="UnityEngine.JsonUtility"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="UnityEngine.JsonUtility"/> binds public fields only, so the field names below must match
    /// GitHub's JSON keys exactly. Unknown keys in the response are ignored.
    /// </remarks>
    internal static class GitHubModels
    {
        // ---- Responses ----

        /// <summary>The authenticated user (<c>GET /user</c>).</summary>
        [Serializable]
        public class AuthUser
        {
            public long id;
            public string login;
            public string name;
        }

        /// <summary>A repository (<c>GET /repos/{owner}/{repo}</c>).</summary>
        [Serializable]
        public class Repository
        {
            public string full_name;
            public string html_url;
        }

        /// <summary>Response carrying a created issue's number and URL.</summary>
        [Serializable]
        public class CreatedIssue
        {
            public long number;
            public string html_url;
        }

        /// <summary>Response carrying a created release's id and URL.</summary>
        [Serializable]
        public class CreatedRelease
        {
            public long id;
            public string html_url;
        }

        // ---- Request payloads ----

        /// <summary>Body for <c>POST /repos/{owner}/{repo}/issues</c>.</summary>
        [Serializable]
        public class CreateIssueRequest
        {
            public string title;
            public string body;
        }

        /// <summary>Body for <c>POST /repos/{owner}/{repo}/releases</c>.</summary>
        [Serializable]
        public class CreateReleaseRequest
        {
            public string tag_name;
            public string name;
            public string body;
        }
    }
}

