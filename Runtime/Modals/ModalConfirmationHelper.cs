using UnityEngine;
using UnityEngine.Events;
using Molca.Localization;

namespace Molca.Modals
{
    public class ModalConfirmationHelper : MonoBehaviour
    {
        [System.Serializable]
        public class ConfirmationData
        {
            public DynamicLocalization title;
            public DynamicLocalization subtitle;
            public DynamicLocalization message;
            public DynamicLocalization details;
            public DynamicLocalization yesText;
            public DynamicLocalization noText;
            [Space, Header("Options")]
            public bool showNoButton;
            public bool useAdvancedModal;
            public bool allowMultiple;
            public bool closeAllModals;
            public ModalConfirmation confirmationPrefab;
        }

        public ConfirmationData confirmationData;

        [Space, Header("Callbacks")]
        public UnityEvent confirmCallback;
        public UnityEvent cancelCallback;

        [Inject] private ModalManager _modalMgr;

        private ModalConfirmation _modal;
        private const string LOCALE_KEY_PREFIX = "_Confirmation";

        private async void Start()
        {
            await RuntimeManager.WaitForInitialization();
            if (this == null) return;

            // _modalMgr is populated by [Inject] during WaitForInitialization.
            if (_modalMgr == null)
                _modalMgr = RuntimeManager.GetSubsystem<ModalManager>();

            if (confirmationData.title != null)    confirmationData.title.Init($"{LOCALE_KEY_PREFIX}.{RandomStringGenerator.GenerateGuid()}");
            if (confirmationData.subtitle != null)  confirmationData.subtitle.Init($"{LOCALE_KEY_PREFIX}.{RandomStringGenerator.GenerateGuid()}");
            if (confirmationData.message != null)   confirmationData.message.Init($"{LOCALE_KEY_PREFIX}.{RandomStringGenerator.GenerateGuid()}");
            if (confirmationData.details != null)   confirmationData.details.Init($"{LOCALE_KEY_PREFIX}.{RandomStringGenerator.GenerateGuid()}");
            if (confirmationData.yesText != null)   confirmationData.yesText.Init($"{LOCALE_KEY_PREFIX}.{RandomStringGenerator.GenerateGuid()}");
            if (confirmationData.noText != null)    confirmationData.noText.Init($"{LOCALE_KEY_PREFIX}.{RandomStringGenerator.GenerateGuid()}");
        }

        [ContextMenu("Create")]
        public void Create()
        {
            if (_modalMgr == null)
            {
                Debug.LogWarning("ModalConfirmationHelper: ModalManager not ready yet.");
                return;
            }

            if (confirmationData.closeAllModals)
                _modalMgr.CloseAllModals();

            if (_modal != null && !confirmationData.allowMultiple)
                _modal.Close();

            if (string.IsNullOrEmpty(confirmationData.message.String))
            {
                Debug.LogWarning("Cannot create confirmation dialog with empty message.");
                return;
            }

            if (confirmationData.confirmationPrefab != null)
            {
                _modal = _modalMgr.ShowModal(confirmationData.confirmationPrefab) as ModalConfirmation;
                _modal.Setup(
                    confirmationData.title.String,
                    confirmationData.subtitle.String,
                    confirmationData.message.String,
                    confirmationData.details.String,
                    confirmationData.yesText.String,
                    confirmationData.noText.String,
                    () => confirmCallback?.Invoke(),
                    () => cancelCallback?.Invoke(),
                    confirmationData.showNoButton
                );
            }
            else if (confirmationData.useAdvancedModal)
            {
                _modal = _modalMgr.ShowAdvancedConfirmation(
                    confirmationData.title.String,
                    confirmationData.subtitle.String,
                    confirmationData.message.String,
                    confirmationData.details.String,
                    confirmationData.yesText.String,
                    confirmationData.noText.String,
                    () => confirmCallback?.Invoke(),
                    () => cancelCallback?.Invoke(),
                    confirmationData.showNoButton
                );
            }
            else
            {
                _modal = _modalMgr.ShowRegularConfirmation(
                    confirmationData.title.String,
                    confirmationData.message.String,
                    confirmationData.yesText.String,
                    confirmationData.noText.String,
                    () => confirmCallback?.Invoke(),
                    () => cancelCallback?.Invoke(),
                    confirmationData.showNoButton
                );
            }
        }

        /// <summary>Closes the currently open confirmation modal, if any.</summary>
        public void Close()
        {
            if (_modal != null)
                _modal.Close();
        }
    }
}
