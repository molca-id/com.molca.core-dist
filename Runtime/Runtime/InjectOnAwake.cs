using UnityEngine;

namespace Molca
{
    /// <summary>
    /// Helper component that automatically injects dependencies on Awake.
    /// Useful for dynamically instantiated prefabs or objects loaded after RuntimeManager initialization.
    /// </summary>
    [DefaultExecutionOrder(-1000)] // Execute before most scripts
    public class InjectOnAwake : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("If true, will wait for RuntimeManager to be ready before injecting dependencies.")]
        private bool waitForRuntimeManager = true;
        
        private async void Awake()
        {
            if (waitForRuntimeManager && !RuntimeManager.IsReady)
            {
                await RuntimeManager.WaitForInitialization();

                // Destroyed while waiting — GetComponents on a dead object throws.
                if (this == null) return;
            }

            // Inject dependencies into all components on this GameObject
            var components = GetComponents<MonoBehaviour>();
            foreach (var component in components)
            {
                if (component != null && component != this)
                {
                    RuntimeManager.InjectDependencies(component);
                }
            }
        }
    }
}
