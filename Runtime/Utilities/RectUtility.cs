using UnityEngine;
using UnityEngine.UI;

namespace Molca.Utils
{
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-utils.png")]
    [CreateAssetMenu(fileName = "Rect Utility", menuName = "Molca/Utils/Rect Utility", order = 80)]
    public class RectUtility : ScriptableObject
    {
        public async void ForceRebuildLayoutImmediate(RectTransform target)
        {
            await Awaitable.NextFrameAsync();
            LayoutRebuilder.ForceRebuildLayoutImmediate(target);
        }

        public void ClimbParent(RectTransform target)
        {
            target.SetParent(target.parent);
        }
    }
}