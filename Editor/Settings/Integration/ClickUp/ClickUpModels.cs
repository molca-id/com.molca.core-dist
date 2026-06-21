using System;

namespace Molca.Settings.Integration.ClickUp
{
    /// <summary>
    /// Serializable DTOs for the ClickUp v2 REST API, shaped for Unity's <see cref="UnityEngine.JsonUtility"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="UnityEngine.JsonUtility"/> binds public fields only (no properties, no dictionaries), so the
    /// field names below must match ClickUp's JSON keys exactly. Unknown keys in the response are ignored.
    /// </remarks>
    internal static class ClickUpModels
    {
        // ---- Responses ----

        /// <summary>Envelope for <c>GET /api/v2/user</c>.</summary>
        [Serializable]
        public class UserResponse
        {
            public User user;
        }

        /// <summary>A ClickUp user.</summary>
        [Serializable]
        public class User
        {
            public long id;
            public string username;
            public string email;
        }

        /// <summary>Envelope for <c>GET /api/v2/team</c> (workspaces).</summary>
        [Serializable]
        public class TeamsResponse
        {
            public Team[] teams;
        }

        /// <summary>A ClickUp workspace (called "team" in the API).</summary>
        [Serializable]
        public class Team
        {
            public string id;
            public string name;
        }

        /// <summary>Envelope for <c>GET /api/v2/team/{team_id}/space</c>.</summary>
        [Serializable]
        public class SpacesResponse
        {
            public Space[] spaces;
        }

        /// <summary>A ClickUp space (the level between a workspace and its folders).</summary>
        [Serializable]
        public class Space
        {
            public string id;
            public string name;
        }

        /// <summary>Envelope for <c>GET /api/v2/space/{space_id}/folder</c>.</summary>
        [Serializable]
        public class FoldersResponse
        {
            public Folder[] folders;
        }

        /// <summary>Response from creating a task or comment that carries an id and url.</summary>
        [Serializable]
        public class CreatedResponse
        {
            public string id;
            public string url;
        }

        /// <summary>Envelope for <c>GET /api/v2/folder/{folder_id}</c>.</summary>
        /// <remarks>
        /// A folder only carries its own <see cref="statuses"/> when it overrides statuses; otherwise the
        /// authoritative status set lives on each <see cref="FolderList"/>. Callers should fall back to the
        /// list-level statuses when the folder set is empty.
        /// </remarks>
        [Serializable]
        public class Folder
        {
            public string id;
            public string name;
            public bool override_statuses;
            public TaskStatus[] statuses;
            public FolderList[] lists;
        }

        /// <summary>A list inside a folder (subset of the ClickUp list object).</summary>
        [Serializable]
        public class FolderList
        {
            public string id;
            public string name;
            public TaskStatus[] statuses;
        }

        /// <summary>A ClickUp status definition (the set a task can move between).</summary>
        [Serializable]
        public class TaskStatus
        {
            public string status;
            public string color;
            public string type;
            public int orderindex;
        }

        /// <summary>Envelope for <c>GET /api/v2/team/{team_id}/task</c> (the filtered team view).</summary>
        [Serializable]
        public class TasksResponse
        {
            public ClickUpTask[] tasks;
            public bool last_page;
        }

        /// <summary>A ClickUp task (subset used by the editor task list).</summary>
        [Serializable]
        public class ClickUpTask
        {
            public string id;
            public string name;
            public string url;
            public TaskStatus status;
            public User[] assignees;
            public TaskList list;
        }

        /// <summary>The list a task belongs to (subset of the ClickUp list object).</summary>
        [Serializable]
        public class TaskList
        {
            public string id;
            public string name;
        }

        // ---- Request payloads ----

        /// <summary>Body for <c>PUT /api/v2/task/{task_id}</c> when changing a task's status.</summary>
        [Serializable]
        public class UpdateTaskStatusRequest
        {
            public string status;
        }

        /// <summary>Body for <c>POST /api/v2/list/{list_id}/task</c>.</summary>
        [Serializable]
        public class CreateTaskRequest
        {
            public string name;
            public string markdown_description;
        }

        /// <summary>Body for <c>POST /api/v2/task/{task_id}/comment</c>.</summary>
        [Serializable]
        public class CreateCommentRequest
        {
            public string comment_text;
            public bool notify_all;
        }
    }
}
