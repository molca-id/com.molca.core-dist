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

        /// <summary>
        /// ClickUp's error envelope, returned alongside a 4xx/5xx status.
        /// </summary>
        /// <remarks>
        /// Shape is <c>{"err":"Status not found","ECODE":"CAT_014"}</c>. Surfacing <c>err</c> is the difference
        /// between telling the user "400 Bad Request" and "Status not found" — the status code alone never
        /// explains which of a request's several possible mistakes was made.
        /// </remarks>
        [Serializable]
        public class ErrorResponse
        {
            public string err;
            public string ECODE;
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
        /// <remarks>
        /// <see cref="due_date"/> and <see cref="date_updated"/> are Unix epoch <b>milliseconds carried as JSON
        /// strings</b> (e.g. <c>"1508369194377"</c>), not numbers — typing them as <c>long</c> would make
        /// <see cref="UnityEngine.JsonUtility"/> drop them. Use <see cref="ClickUpTaskFormat"/> to parse.
        /// </remarks>
        [Serializable]
        public class ClickUpTask
        {
            public string id;
            public string name;
            public string url;
            public TaskStatus status;
            public User[] assignees;
            public TaskList list;
            public Priority priority;
            public Tag[] tags;
            public string due_date;
            public string date_updated;
        }

        /// <summary>A ClickUp task priority.</summary>
        /// <remarks>
        /// Every field is a JSON string in ClickUp's responses — including <see cref="orderindex"/>, which is
        /// <c>"1"</c> rather than <c>1</c>. The whole object is <c>null</c> on a task with no priority set.
        /// </remarks>
        [Serializable]
        public class Priority
        {
            public string id;
            public string priority;
            public string color;
            public string orderindex;
        }

        /// <summary>A ClickUp tag, carrying its own foreground/background colors.</summary>
        [Serializable]
        public class Tag
        {
            public string name;
            public string tag_fg;
            public string tag_bg;
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
