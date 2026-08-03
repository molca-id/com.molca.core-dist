using System.Collections.Generic;
using UnityEngine;
using Molca;
using Molca.Audio;

namespace Molca.App.Preload
{
    [System.Serializable]
    public class SplashScreenData
    {
        public CanvasGroup splashScreen;
        public float holdDuration;
        public AudioReference chime;
    }

    public class PreloadCheck : MonoBehaviour
    {
        [SerializeField] private bool autoLoadNextScene = true;
        [SerializeField] private float fadeSpeed = 1f;
        [SerializeField] private CanvasGroup background;

        [SerializeField] private List<SplashScreenData> splashScreens = new List<SplashScreenData>();
        [SerializeField] private List<MonoBehaviour> customChecks = new List<MonoBehaviour>();
        [SerializeField] private bool addProjectLogoSplash = true;
        [SerializeField] private float projectLogoHoldDuration = 1.5f;
        [SerializeField] private AudioReference projectLogoChime;

        // Instance API of the scene-loading subsystem (Sprint 5.1 de-static).
        private ISceneLoader _sceneLoader;

        private async void Start()
        {
            try
            {
            await RuntimeManager.WaitForInitialization();
            _sceneLoader = RuntimeManager.GetService<ISceneLoader>();

            // Optionally add project logo splash screen
            if (addProjectLogoSplash && MolcaProjectSettings.Instance != null && MolcaProjectSettings.Instance.ProjectLogo != null)
            {
                // Create GameObject for splash
                var logoGO = new GameObject("ProjectLogoSplash");
                logoGO.transform.SetParent(background.transform.parent, false);
                var canvasGroup = logoGO.AddComponent<CanvasGroup>();
                canvasGroup.alpha = 0f;
                var image = logoGO.AddComponent<UnityEngine.UI.Image>();
                image.sprite = MolcaProjectSettings.Instance.ProjectLogo;
                image.preserveAspect = true;
                image.raycastTarget = false;
                // Optionally, stretch to full screen (assuming parent Canvas is set up)
                var rect = image.GetComponent<RectTransform>();
                // Fill half the screen horizontally, full height
                rect.anchorMin = new Vector2(0.25f, 0f); // left at 25%
                rect.anchorMax = new Vector2(0.75f, 1f); // right at 75%
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                // Add to splashScreens at the start
                var logoSplash = new SplashScreenData
                {
                    splashScreen = canvasGroup,
                    holdDuration = projectLogoHoldDuration,
                    chime = projectLogoChime
                };
                splashScreens.Insert(0, logoSplash);
                Debug.Log("Project Logo Splash Screen Added");
            }
            // Initialize all splash screens to be invisible
            foreach (var splash in splashScreens)
            {
                splash.splashScreen.alpha = 0f;
            }

            await RuntimeManager.WaitForInitialization();
            _sceneLoader = RuntimeManager.GetService<ISceneLoader>();

            // Run splash screens first
            await RunSplashScreensAsync();

            // Then run custom checks sequentially
            await RunCustomChecksAsync();

            if(autoLoadNextScene)
            {
                string preloadSceneName = _sceneLoader.ActiveScene.name;

                await LoadNextSceneWithCallback();
                await FadeOut(background);

                _ = _sceneLoader.UnloadScene(preloadSceneName);
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

        private async Awaitable RunCustomChecksAsync()
        {
            foreach (var check in customChecks)
            {
                if (check is IPreloadCheck preloadCheck)
                {
                    await preloadCheck.RunCheck();
                }
                else
                {
                    Debug.LogWarning($"Check {check.name} does not implement IPreloadCheck interface");
                }
            }
        }

        private async Awaitable RunSplashScreensAsync()
        {
            foreach (var splash in splashScreens)
            {
                splash.chime.Play();
                await FadeIn(splash.splashScreen);
                await Awaitable.WaitForSecondsAsync(splash.holdDuration);
                await FadeOut(splash.splashScreen);
            }
        }

        private async Awaitable FadeIn(CanvasGroup target)
        {
            float alpha = 0f;
            while (alpha < 1f)
            {
                alpha += Time.deltaTime * fadeSpeed;
                target.alpha = alpha * alpha;
                await Awaitable.NextFrameAsync();
            }
            target.alpha = 1f;
        }

        private async Awaitable FadeOut(CanvasGroup target)
        {
            float alpha = 1f;
            while (alpha > 0f)
            {
                alpha -= Time.deltaTime * fadeSpeed;
                target.alpha = alpha * alpha;
                await Awaitable.NextFrameAsync();
            }
            target.alpha = 0f;
        }

        private async Awaitable LoadNextSceneWithCallback()
        {
        bool sceneLoaded = false;
        _sceneLoader.LoadNextScene(UnityEngine.SceneManagement.LoadSceneMode.Additive, 
            (scene) => sceneLoaded = true);
            
        while (!sceneLoaded)
                await Awaitable.NextFrameAsync();
        }
    }
}
