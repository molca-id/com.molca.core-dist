using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.SceneManagement;

namespace Molca.Utils
{
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-utils.png")]
    [CreateAssetMenu(fileName = "Scene Utility", menuName = "Molca/Utils/Scene Utility", order = 80)]
    public class SceneUtility : ScriptableObject
    {
        // UnityEvent-friendly wrappers over the scene-loading instance API.
        private static ISceneLoader Loader => RuntimeManager.GetService<ISceneLoader>();

        public void LoadScene(string sceneName)
        {
            Loader?.LoadScene(sceneName, LoadSceneMode.Single);
        }

        public void LoadScene(SharedString sharedString)
        {
            Loader?.LoadScene(sharedString.value, LoadSceneMode.Single);
        }

        public void LoadScene(AssetReference sceneRef)
        {
            Loader?.LoadAddressableScene(sceneRef);
        }

        public void LoadSceneAdditive(string sceneName)
        {
            Loader?.LoadScene(sceneName, LoadSceneMode.Additive);
        }

        public void LoadSceneAdditive(SharedString sharedString)
        {
            Loader?.LoadScene(sharedString.value, LoadSceneMode.Additive);
        }
        
        public void LoadSceneAdditive(AssetReference sceneRef)
        {
            Loader?.LoadAddressableScene(sceneRef, LoadSceneMode.Additive);
        }

        public void LoadSceneByAddress(string address)
        {
            Addressables.LoadSceneAsync(address, LoadSceneMode.Single);
        }

        public void LoadSceneAdditiveByAddress(string address)
        {
            Addressables.LoadSceneAsync(address, LoadSceneMode.Additive);
        }

        public void LoadNextScene()
        {
            Loader?.LoadNextScene();
        }

        public void QuitApplication()
        {
            Application.Quit();
        }
    }
}