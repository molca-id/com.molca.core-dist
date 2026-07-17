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
            // async-void entry point: nothing may escape into Unity's sync context.
            try
            {
                if (waitForRuntimeManager && !RuntimeManager.IsReady)
                {
                    await RuntimeManager.WaitForInitialization();

                    // Destroyed while waiting — GetComponents on a dead object throws.
                    if (this == null) return;
                }

                // Inject dependencies into all components on this GameObject.
                // Per-component isolation: one component with an unresolvable
                // required [Inject] must not abort injection for its siblings.
                var components = GetComponents<MonoBehaviour>();
                foreach (var component in components)
                {
                    if (component != null && component != this)
                    {
                        try
                        {
                            RuntimeManager.InjectDependencies(component);
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogError($"InjectOnAwake: failed to inject {component.GetType().Name} on '{name}': {e.Message}", component);
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"InjectOnAwake failed on '{name}': {e.Message}", this);
            }
        }
    }
}
