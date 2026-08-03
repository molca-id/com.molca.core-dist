using UnityEngine;
using UnityEngine.Events;

namespace Molca.App.Preload
{
    public class FirstLaunchCheck : MonoBehaviour, IPreloadCheck
    {
        private const string FIRST_LAUNCH_KEY = "FirstLaunch";
        
        [SerializeField] private bool forceFirstLaunch = false; // For testing purposes
        [SerializeField, Tooltip("Delay in seconds before first launch is completed automatically. Only used when greater than zero. At zero or less, preload waits until CompleteFirstLaunch() is called (e.g. from UI).")]
        private float autoCompleteDelay = 0.5f;
        [SerializeField] private UnityEvent onFirstLaunch;

        private bool _firstLaunchCompleted = false;
        private bool CanCompleteFirstLaunch() => !_firstLaunchCompleted || forceFirstLaunch;

        public async Awaitable RunCheck()
        {
            _firstLaunchCompleted = PlayerPrefs.GetInt(FIRST_LAUNCH_KEY, 0) == 1;
            
            if (CanCompleteFirstLaunch())
            {
                Debug.Log("First time launch detected!");
                // You can add any first-time setup logic here
                // For example: setting default settings, creating necessary files, etc.

                onFirstLaunch?.Invoke();

                if (autoCompleteDelay > 0f)
                {
                    await Awaitable.WaitForSecondsAsync(autoCompleteDelay);
                    if (!_firstLaunchCompleted)
                        CompleteFirstLaunch();
                }
                else
                {
                    while (!_firstLaunchCompleted)
                        await Awaitable.WaitForSecondsAsync(0.25f);
                }
            }
            else
            {
                Debug.Log("Not first launch");
            }
        }

        public void CompleteFirstLaunch()
        {
            PlayerPrefs.SetInt(FIRST_LAUNCH_KEY, 1);
            PlayerPrefs.Save();
            _firstLaunchCompleted = true;
        }
    } 
}