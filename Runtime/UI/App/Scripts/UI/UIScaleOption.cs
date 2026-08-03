using Molca;
using Molca.Settings;
using UnityEngine;
using System.Linq;

namespace Molca.App.UI
{
    public class UIScaleOption : MonoBehaviour
    {
        [System.Serializable]
        public struct ScaleOption
        {
            public float scale;
            public ButtonState buttonState;
        }

        [SerializeField] private ScaleOption[] scaleOptions;

        // Start is called once before the first execution of Update after the MonoBehaviour is created
        private async void Start()
        {
            try
            {
                await RuntimeManager.WaitForInitialization();
                await Awaitable.WaitForSecondsAsync(0.1f);

                var scaleModule = GlobalSettings.GetModule<CanvasScaleModule>();
                foreach (var option in scaleOptions)
                {
                    if(scaleModule.UIScaleNormalized == option.scale)
                    {
                        option.buttonState.SetState(true);
                    }
                    option.buttonState.onClicked += OnScaleOptionClicked;
                }
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

        private void OnScaleOptionClicked(ButtonState buttonState)
        {
            var scaleOption = scaleOptions.FirstOrDefault(option => option.buttonState == buttonState);
            if (scaleOption.buttonState == null) return;

            var scaleModule = GlobalSettings.GetModule<CanvasScaleModule>();
            scaleModule.UIScaleNormalized = scaleOption.scale;
        }
    }
}