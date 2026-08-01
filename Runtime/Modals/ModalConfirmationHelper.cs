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
        private LocalizedValueBinding[] _bindings;

        private async void Start()
        {
            try
            {
                await RuntimeManager.WaitForInitialization();
                if (this == null) return;

                // _modalMgr is populated by [Inject] during WaitForInitialization.
                if (_modalMgr == null)
                    _modalMgr = RuntimeManager.GetSubsystem<ModalManager>();

                if (confirmationData == null)
                    return;
                var values = new[]
                {
                    confirmationData.title,
                    confirmationData.subtitle,
                    confirmationData.message,
                    confirmationData.details,
                    confirmationData.yesText,
                    confirmationData.noText,
                };
                _bindings = new LocalizedValueBinding[values.Length];
                for (var index = 0; index < values.Length; index++)
                {
                    if (values[index] == null)
                        continue;
                    var binding = values[index].CreateBinding();
                    binding.ValueChanged += OnBoundValueChanged;
                    _bindings[index] = binding;
                    await binding.RefreshAsync();
                    if (this == null)
                        return;
                }
            }
            catch (System.Exception exception)
            {
                if (this != null)
                    Debug.LogException(exception, this);
            }
        }

        private void OnDestroy()
        {
            if (_bindings == null)
                return;
            foreach (var binding in _bindings)
            {
                if (binding == null)
                    continue;
                binding.ValueChanged -= OnBoundValueChanged;
                binding.Dispose();
            }
            _bindings = null;
        }

        [ContextMenu("Create")]
        public void Create()
        {
            if (_modalMgr == null)
            {
                Debug.LogWarning("ModalConfirmationHelper: ModalManager not ready yet.");
                return;
            }
            if (confirmationData == null)
            {
                Debug.LogWarning("ModalConfirmationHelper: Confirmation data is missing.");
                return;
            }

            if (confirmationData.closeAllModals)
                _modalMgr.CloseAllModals();

            if (_modal != null && !confirmationData.allowMultiple)
                _modal.Close();

            if (string.IsNullOrEmpty(Value(2)))
            {
                Debug.LogWarning("Cannot create confirmation dialog with empty message.");
                return;
            }

            if (confirmationData.confirmationPrefab != null)
                _modal = _modalMgr.ShowModal(confirmationData.confirmationPrefab) as ModalConfirmation;
            else if (confirmationData.useAdvancedModal)
                _modal = _modalMgr.ShowAdvancedConfirmation(
                    Value(0), Value(1), Value(2), Value(3), Value(4), Value(5),
                    () => confirmCallback?.Invoke(),
                    () => cancelCallback?.Invoke(),
                    confirmationData.showNoButton
                );
            else
                _modal = _modalMgr.ShowRegularConfirmation(
                    Value(0), Value(2), Value(4), Value(5),
                    () => confirmCallback?.Invoke(),
                    () => cancelCallback?.Invoke(),
                    confirmationData.showNoButton
                );
            ApplyCurrentModal();
        }

        private string Value(int index)
        {
            if (_bindings != null && index >= 0 && index < _bindings.Length &&
                _bindings[index] != null)
                return _bindings[index].LastSuccessfulResult;
            return index switch
            {
                0 => confirmationData?.title?.String ?? string.Empty,
                1 => confirmationData?.subtitle?.String ?? string.Empty,
                2 => confirmationData?.message?.String ?? string.Empty,
                3 => confirmationData?.details?.String ?? string.Empty,
                4 => confirmationData?.yesText?.String ?? string.Empty,
                5 => confirmationData?.noText?.String ?? string.Empty,
                _ => string.Empty,
            };
        }

        private void OnBoundValueChanged(string _) => ApplyCurrentModal();

        private void ApplyCurrentModal()
        {
            if (_modal == null || confirmationData == null)
                return;
            _modal.Setup(
                Value(0),
                Value(1),
                Value(2),
                Value(3),
                Value(4),
                Value(5),
                () => confirmCallback?.Invoke(),
                () => cancelCallback?.Invoke(),
                confirmationData.showNoButton);
        }

        /// <summary>Closes the currently open confirmation modal, if any.</summary>
        public void Close()
        {
            if (_modal != null)
                _modal.Close();
        }
    }
}
