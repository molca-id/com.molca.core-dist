using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>
    /// Borrows the editor's scene setup so an audit can read closed scenes, and puts it back afterwards.
    /// </summary>
    /// <remarks>
    /// Reading a closed scene means opening it, which replaces whatever the user had open. That is
    /// acceptable only if it can be undone exactly, so the session refuses to begin when any open scene
    /// has no asset path (an untitled or in-memory scene cannot be reopened) — the audit then reports the
    /// declared scenes as unscanned coverage instead of destroying unsaved work.
    ///
    /// The session never saves anything. Modified open scenes are also grounds for refusal: silently
    /// discarding them would be data loss, and silently saving them would make a read-only audit write.
    /// </remarks>
    internal sealed class ReferenceSceneScanSession
    {
        private readonly SceneSetup[] _setup;

        private ReferenceSceneScanSession(SceneSetup[] setup) => _setup = setup;

        /// <summary>
        /// Captures the current scene setup.
        /// </summary>
        /// <param name="session">The session to <see cref="Restore"/> when scanning finishes.</param>
        /// <param name="refusalReason">Why the session could not begin. Empty on success.</param>
        /// <returns>True when scenes may be opened for scanning.</returns>
        public static bool TryBegin(out ReferenceSceneScanSession session, out string refusalReason)
        {
            session = null;
            refusalReason = string.Empty;

            var setup = EditorSceneManager.GetSceneManagerSetup();
            if (setup == null || setup.Length == 0)
            {
                refusalReason = "no scene is open, so the editor's scene setup could not be captured and restored";
                return false;
            }

            var untitled = setup.FirstOrDefault(s => string.IsNullOrEmpty(s.path));
            if (untitled != null)
            {
                refusalReason = "an open scene has never been saved, so it could not be restored after scanning";
                return false;
            }

            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.isDirty)
                {
                    refusalReason = $"scene '{scene.name}' has unsaved changes; save it and re-run so the audit does not have to discard or save them";
                    return false;
                }
            }

            session = new ReferenceSceneScanSession(setup);
            return true;
        }

        /// <summary>
        /// Restores the captured scene setup. Safe to call once; failures are logged, never thrown, so a
        /// restore problem cannot mask the audit result the caller is about to return.
        /// </summary>
        public void Restore()
        {
            try
            {
                EditorSceneManager.RestoreSceneManagerSetup(_setup);
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    $"[ReferenceAudit] Could not fully restore the previously open scenes after scanning: {e.Message}");
            }
        }
    }
}
