using UnityEngine;
using UnityEngine.UI;

namespace Molca.Utils
{
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-utils.png")]
    [CreateAssetMenu(fileName = "Rect Utility", menuName = "Molca/Utils/Rect Utility", order = 80)]
    public class RectUtility : ScriptableObject
    {
        /// <summary>
        /// Rebuilds <paramref name="target"/>'s layout one frame later (after pending
        /// layout changes have settled). Fire-and-forget; exceptions are contained here.
        /// </summary>
        public async void ForceRebuildLayoutImmediate(RectTransform target) // doctor:ignore async-void is intentional: UnityEvent/inspector-friendly fire-and-forget, body owns its exceptions via try/catch
        {
            try
            {
                await Awaitable.NextFrameAsync();
                // The target may have been destroyed during the one-frame wait.
                if (target == null) return;
                LayoutRebuilder.ForceRebuildLayoutImmediate(target);
            }
            catch (System.OperationCanceledException) { }
            catch (System.Exception e)
            {
                Debug.LogError($"[RectUtility] ForceRebuildLayoutImmediate failed: {e}");
            }
        }

        public void ClimbParent(RectTransform target)
        {
            target.SetParent(target.parent);
        }
    }
}