using UnityEngine;

namespace Molca.Utils
{
    [System.Serializable]
    public class SceneReference : ISerializationCallbackReceiver
    {
        // In the editor, we use a SceneAsset object field.
        // This is hidden from the build because UnityEditor classes
        // cannot be included in a final game build.
    #if UNITY_EDITOR
        [SerializeField]
        private UnityEditor.SceneAsset sceneAsset = null;
    #endif

        // This is the string path we serialize at build time.
        // It's hidden in the inspector because we use the
        // sceneAsset field to set it.
        [HideInInspector]
        [SerializeField]
        private string scenePath = string.Empty;

        // This property lets you easily get the scene path at runtime.
        public string ScenePath
        {
            get { return scenePath; }
        }

        // Allows you to use a SceneReference object as a string.
        // Example: SceneManager.LoadScene(mySceneReference);
        public static implicit operator string(SceneReference sceneReference)
        {   
            return sceneReference.ScenePath;
        }

        // Called before Unity serializes this object.
        public void OnBeforeSerialize()
        {
    #if UNITY_EDITOR
            // In the editor, get the path from the SceneAsset
            if (sceneAsset != null)
            {
                scenePath = UnityEditor.AssetDatabase.GetAssetPath(sceneAsset);
            }
            else
            {
                scenePath = string.Empty;
            }
    #endif
        }

        // Called after Unity deserializes this object.
        public void OnAfterDeserialize()
        {
            // We don't need to do anything here for runtime,
            // but the interface requires the method.
        }
    }
}
