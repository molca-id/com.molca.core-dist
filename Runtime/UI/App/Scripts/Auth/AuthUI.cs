using UnityEngine;
using Molca.Networking.Auth;
using Molca;
using TMPro;
using UnityEngine.UI;
using Molca.Modals;
using Molca.Utils;

namespace Molca.App.Auth
{
    public class AuthUI : MonoBehaviour
    {
        // Instance API of the scene-loading subsystem (Sprint 5.1 de-static).
        private static ISceneLoader SceneLoader => RuntimeManager.GetService<ISceneLoader>();
        [SerializeField] private SharedString nextSceneName;
        [SerializeField] private TMP_InputField usernameInput;
        [SerializeField] private TMP_InputField passwordInput;
        [SerializeField] private TMP_Text usernameErrorText;
        [SerializeField] private TMP_Text passwordErrorText;

        [SerializeField] private Button loginButton;
        [SerializeField] private Button forgotPasswordButton;
        [SerializeField] private Button ssoLoginButton;

        [Header("Guest Login")]
        [SerializeField] private GameObject guestPanel;
        [SerializeField] private TMP_InputField guestUsernameInput;
        [SerializeField] private TMP_Text guestUsernameErrorText;
        [SerializeField] private Button guestPanelButton;
        [SerializeField] private Button guestLoginButton;
        [SerializeField] private Button guestLoginCancelButton;

        private async void Start()
        {
            try
            {
                await RuntimeManager.WaitForInitialization();
                AuthEvents.LoggedIn.Register(OnAuthLoggedIn);

                loginButton.onClick.AddListener(OnLoginClicked);
                guestPanelButton.onClick.AddListener(OnGuestPanelClicked);
                guestLoginCancelButton.onClick.AddListener(OnGuestCancelClicked);
                guestLoginButton.onClick.AddListener(OnGuestClicked);
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
            AuthEvents.LoggedIn.Unregister(OnAuthLoggedIn);
        }

        private void OnAuthLoggedIn(AuthLoggedInEventData data)
        {
            SceneLoader?.LoadScene(nextSceneName.value);
        }

        public async void OnLoginClicked()
        {
            try
            {
                var success = await RuntimeManager.GetSubsystem<AuthManager>().LoginAsync(usernameInput.text, passwordInput.text);
                if (!success)
                {
                    usernameErrorText.text = "Invalid username or password";
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

        public void OnGuestClicked()
        {
            RuntimeManager.GetSubsystem<ModalManager>().ShowRegularConfirmation(null, $"Guest login as {guestUsernameInput.text}?", "Yes", "No", () =>
            {
                RuntimeManager.GetSubsystem<AuthManager>().GuestLogin(new SDKUserData("Guest", guestUsernameInput.text));
            }, null, true);
        }

        public void OnGuestPanelClicked()
        {
            guestPanel.SetActive(true);
        }

        public void OnGuestCancelClicked()
        {
            guestPanel.SetActive(false);
        }
    }
}