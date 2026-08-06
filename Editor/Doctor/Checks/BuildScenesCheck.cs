using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Validates the Editor Build Settings scene list: enabled scenes whose files are
    /// missing or moved (the player build would fail), duplicate enabled entries, and an
    /// empty or all-disabled list (a build would contain no scenes).
    /// </summary>
    /// <remarks>
    /// This is distinct from <see cref="SceneObjectReferenceCheck"/>, which validates the
    /// reference ids inside scenes; this check validates the build list itself. It stays on
    /// the main thread (<see cref="EditorBuildSettings"/> / <see cref="AssetDatabase"/> are
    /// main-thread only).
    /// </remarks>
    public class BuildScenesCheck : IDoctorCheck
    {
        public string Id => "build-scenes-valid";
        public string Description => "Build-list scenes exist, are not duplicated, and at least one is enabled";

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            await Awaitable.MainThreadAsync();
            var issues = new List<DoctorIssue>();

            var enabledPaths = new List<string>();
            foreach (var scene in EditorBuildSettings.scenes)
            {
                if (!scene.enabled)
                    continue;

                var path = scene.path;
                if (string.IsNullOrEmpty(path) || AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                {
                    issues.Add(new DoctorIssue(Id, DoctorSeverity.Error,
                        $"Build Settings list an enabled scene that does not exist: \"{path}\". Remove the entry or restore the scene file.",
                        string.IsNullOrEmpty(path) ? null : path));
                    continue;
                }

                enabledPaths.Add(path);
            }

            foreach (var dup in enabledPaths.GroupBy(p => p).Where(g => g.Count() > 1))
            {
                issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                    $"Scene \"{dup.Key}\" is listed more than once among the enabled build scenes.", dup.Key));
            }

            // Only a finding when something would actually build from this list. A project whose every
            // profile declares its own scene set does not use the Build Settings list at all, and warning
            // about it there would be a permanent false positive in a gate people are meant to trust.
            if (enabledPaths.Count == 0 && AnyProfileUsesTheBuildSettingsList())
            {
                issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                    "No enabled scenes in Build Settings, and at least one build profile declares no scene " +
                    "set of its own — a player build from that profile would contain no scenes."));
            }

            return issues;
        }

        /// <summary>
        /// True when any build profile in the project would build the Editor Build Settings scene list —
        /// i.e. declares no scene set of its own. Also true when there are no profiles at all, since
        /// <c>File &gt; Build</c> always uses the list.
        /// </summary>
        private static bool AnyProfileUsesTheBuildSettingsList()
        {
            var guids = AssetDatabase.FindAssets("t:BuildSettings");
            if (guids.Length == 0)
                return true;

            bool sawProfile = false;
            foreach (var guid in guids)
            {
                var settings = AssetDatabase.LoadAssetAtPath<Molca.Settings.BuildSettings>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (settings == null)
                    continue;

                foreach (var profile in settings.Profiles)
                {
                    if (profile == null)
                        continue;

                    sawProfile = true;
                    if (!profile.HasSceneOverride)
                        return true;
                }
            }

            return !sawProfile;
        }
    }
}
