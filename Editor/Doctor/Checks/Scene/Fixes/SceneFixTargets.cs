using System;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Helpers for interpreting an <see cref="DoctorIssue.Path"/> as a scene-fix target (Sprint 55). Scene
    /// checks encode scene-object findings as <c>"scenePathOrName :: hierarchy/path"</c>; asset findings use
    /// the plain asset path.
    /// </summary>
    internal static class SceneFixTargets
    {
        /// <summary>Returns the hierarchy-path portion of a scene-object finding target (after <c>"::"</c>), or the trimmed input.</summary>
        public static string HierarchyPathOf(string target)
        {
            if (string.IsNullOrEmpty(target)) return null;
            var idx = target.IndexOf("::", StringComparison.Ordinal);
            return (idx >= 0 ? target.Substring(idx + 2) : target).Trim();
        }

        /// <summary>Resolves the scene <see cref="GameObject"/> a finding target points at, via <see cref="GameObjectEditingService"/>.</summary>
        public static GameObject ResolveGameObject(string target, out string error)
            => GameObjectEditingService.Resolve(HierarchyPathOf(target), out error);
    }
}
