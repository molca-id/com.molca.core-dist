using System;

namespace Molca.Settings.Integration.Figma
{
    /// <summary>
    /// Serializable Figma REST API response models for <see cref="UnityEngine.JsonUtility"/>.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/Figma/</c>.
    /// Registration: plain data; not assets. Field names match Figma JSON keys exactly so
    /// <see cref="UnityEngine.JsonUtility"/> binds them without remapping.
    /// <para>
    /// Only the <b>flat</b> list endpoints (<c>/v1/me</c>, team projects, project files) are modeled here —
    /// they have stable, statically-keyed shapes. The deeply-nested, polymorphic <b>node tree</b>
    /// (<c>/v1/files/:key/nodes</c>, <c>/v1/images</c>) is handled as Newtonsoft <c>JToken</c> in
    /// <see cref="FigmaApiClient"/> and <see cref="FigmaToUiToolkitTranslator"/>, because Figma node JSON is
    /// recursive, uses dynamic object keys (node-id-keyed maps), and contains node types the translator must
    /// inspect generically rather than bind to fixed fields.
    /// </para>
    /// </remarks>
    public static class FigmaModels
    {
        /// <summary>The authenticated Figma user (<c>GET /v1/me</c>) — used to validate the token.</summary>
        [Serializable]
        public class FigmaUser
        {
            /// <summary>The user's stable id.</summary>
            public string id;
            /// <summary>The user's email address.</summary>
            public string email;
            /// <summary>The user's display handle.</summary>
            public string handle;
            /// <summary>URL of the user's avatar image.</summary>
            public string img_url;
        }

        /// <summary>A Figma project within a team (<c>GET /v1/teams/:team_id/projects</c>).</summary>
        [Serializable]
        public class FigmaProject
        {
            /// <summary>The project's id (used to list its files).</summary>
            public string id;
            /// <summary>The project's display name.</summary>
            public string name;
        }

        /// <summary>The projects-in-team response.</summary>
        [Serializable]
        public class ProjectsResponse
        {
            /// <summary>The team's display name.</summary>
            public string name;
            /// <summary>The projects in the team.</summary>
            public FigmaProject[] projects;
        }

        /// <summary>A Figma file within a project (<c>GET /v1/projects/:project_id/files</c>).</summary>
        [Serializable]
        public class FigmaFile
        {
            /// <summary>The file key (used in every other file/node/image endpoint).</summary>
            public string key;
            /// <summary>The file's display name.</summary>
            public string name;
            /// <summary>URL of the file's thumbnail render.</summary>
            public string thumbnail_url;
            /// <summary>ISO-8601 last-modified timestamp.</summary>
            public string last_modified;
        }

        /// <summary>The files-in-project response.</summary>
        [Serializable]
        public class FilesResponse
        {
            /// <summary>The project's display name.</summary>
            public string name;
            /// <summary>The files in the project.</summary>
            public FigmaFile[] files;
        }
    }
}
