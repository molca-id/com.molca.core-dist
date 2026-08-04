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
    /// acceptable only if it can be undone exactly, so the session refuses to begin when an open scene has
    /// unsaved changes — silently discarding them would be data loss, and silently saving them would make
    /// a read-only audit write. The audit then reports the declared scenes as unscanned coverage.
    ///
    /// <para><b>An empty setup is not a refusal.</b> With no saved scene open there is nothing to put back,
    /// which is the easiest case to restore, not an impossible one. It used to refuse — with the reason
    /// "no scene is open, so the editor's scene setup could not be captured and restored" — which meant a
    /// developer working from the default untitled scene could never run a Full audit at all, and, because
    /// that refusal was recorded as a <i>failed</i> coverage category, was left with a permanently stale
    /// snapshot that blocked every repair.</para>
    ///
    /// The session never saves anything.
    /// </remarks>
    internal sealed class ReferenceSceneScanSession
    {
        private readonly SceneSetup[] _setup;

        /// <summary>
        /// Whether the untitled scene that stood in for a setup held anything.
        /// </summary>
        /// <remarks>
        /// An unsaved scene has no file to reproduce it from, so restoring it is an approximation either
        /// way. Recording whether it was populated is what makes the approximation right in the case that
        /// actually occurs: a developer sitting on Unity's default new scene gets a scene with a camera and
        /// a light back, not a blank one. Only reached when that scene is clean — a dirty one refuses the
        /// session outright — so nothing being reproduced is anything Unity would not itself discard the
        /// next time a scene was opened.
        /// </remarks>
        private readonly bool _untitledSceneHadContent;

        private ReferenceSceneScanSession(SceneSetup[] setup, bool untitledSceneHadContent)
        {
            _setup = setup;
            _untitledSceneHadContent = untitledSceneHadContent;
        }

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

            // Checked first, and against the live scene list rather than against the setup: an untitled
            // scene does not appear in the setup at all, so a check ordered after an early return on an
            // empty setup would not run for the one case where unsaved work is most likely.
            var untitledHadContent = false;

            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;

                if (scene.isDirty)
                {
                    refusalReason =
                        $"scene '{scene.name}' has unsaved changes; save it and re-run so the audit does not "
                        + "have to discard or save them";
                    return false;
                }

                if (string.IsNullOrEmpty(scene.path))
                    untitledHadContent |= scene.rootCount > 0;
            }

            var setup = EditorSceneManager.GetSceneManagerSetup() ?? Array.Empty<SceneSetup>();

            var untitled = setup.FirstOrDefault(s => string.IsNullOrEmpty(s.path));
            if (untitled != null)
            {
                refusalReason = "an open scene has never been saved, so it could not be restored after scanning";
                return false;
            }

            session = new ReferenceSceneScanSession(setup, untitledHadContent);
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
                if (_setup.Length == 0)
                {
                    // Nothing saved was open, so the closest thing to "put it back" is a fresh untitled
                    // scene. RestoreSceneManagerSetup cannot express that — it throws on an empty array —
                    // and leaving the last audited scene open instead would silently hand the user a
                    // project scene they never opened.
                    EditorSceneManager.NewScene(
                        _untitledSceneHadContent ? NewSceneSetup.DefaultGameObjects : NewSceneSetup.EmptyScene,
                        NewSceneMode.Single);
                    return;
                }

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
