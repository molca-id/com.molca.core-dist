using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Molca;
using Molca.Modals;
using Molca.Networking.Auth;
using Molca.App;
using Molca.Utils;
using Molca.App.Auth;

namespace Molca.App.UI
{
    public class ProfileUI : MonoBehaviour
    {
        // Instance API of the scene-loading subsystem (Sprint 5.1 de-static).
        private static ISceneLoader SceneLoader => RuntimeManager.GetService<ISceneLoader>();
        [SerializeField] private SharedString authSceneName;
        [SerializeField] private Button logoutButton;
        [SerializeField] private ModalConfirmationHelper logoutConfirmationHelper;

        [Header("Authenticated")]
        [SerializeField] private GameObject authenticatedPanel;
        [SerializeField] private TMP_Text usernameText;
        [SerializeField] private TMP_Text userIdText;

        [Header("Guest")]
        [SerializeField] private GameObject guestPanel;
        [SerializeField] private TMP_Text guestUsernameText;

        // Start is called once before the first execution of Update after the MonoBehaviour is created
        private async void Start()
        {
            try
            {
                logoutButton.onClick.AddListener(OnLogoutClicked);
                logoutConfirmationHelper.confirmCallback.AddListener(OnLogoutConfirmed);

                await RuntimeManager.WaitForInitialization();
                AuthEvents.LoggedOut.Register(OnAuthLoggedOut);

                var authManager = RuntimeManager.GetSubsystem<AuthManager>();

                if (!authManager.IsAuthenticated) return;

                var userData = authManager.GetUserData<SDKUserData>();
                if (authManager.User.IsGuest)
                {
                    guestPanel.SetActive(true);
                    guestUsernameText.text = userData.Username;
                }
                else
                {
                    authenticatedPanel.SetActive(true);
                    usernameText.text = userData.Username;
                    userIdText.text = userData.UserId;
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

        private void OnDestroy()
        {
            AuthEvents.LoggedOut.Unregister(OnAuthLoggedOut);
        }

        private void OnAuthLoggedOut(AuthLoggedOutEventData data)
        {
            SceneLoader?.LoadScene(authSceneName.value);
        }

        private void OnLogoutClicked()
        {
            logoutConfirmationHelper.Create();
        }

        private async void OnLogoutConfirmed()
        {
            try
            {
                var modalManager = RuntimeManager.GetService<ModalManager>();
                modalManager.ShowFullScreenLoading("Logging out...");
                var success = await RuntimeManager.GetSubsystem<AuthManager>().LogoutAsync();
                modalManager.HideFullScreenLoading();
                if (!success)
                {
                    modalManager.AddMessage("Failed to logout", ModalManager.MessageType.Error);
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
    }
}