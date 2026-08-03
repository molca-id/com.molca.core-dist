using Molca;
using Molca.Settings;
using UnityEngine;
using UnityEngine.UI;

namespace Molca.App.UI
{
    [RequireComponent(typeof(CanvasScaler))]
    public class CanvasScaleListener : MonoBehaviour
    {
        private CanvasScaler canvasScaler;

        async void Start()
        {
            try
            {
                canvasScaler = GetComponent<CanvasScaler>();
                await RuntimeManager.WaitForInitialization();
                var scaleModule = GlobalSettings.GetModule<CanvasScaleModule>();
                canvasScaler.scaleFactor = scaleModule.UIScale;

                scaleModule.onUiScaleChanged += OnUiScaleChanged;
            }
            catch (System.OperationCanceledException)
            {
                // cancellation is not an error — exit quietly
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }

        private void OnDestroy()
        {
            var scaleModule = GlobalSettings.GetModule<CanvasScaleModule>();
            scaleModule.onUiScaleChanged -= OnUiScaleChanged;
        }

        private void OnUiScaleChanged(float scale)
        {
            canvasScaler.scaleFactor = scale;
        }
    }
}