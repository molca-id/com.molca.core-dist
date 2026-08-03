using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;
using Molca.Modals;
using Molca.App.UI;
using Molca.App.Media;

namespace Molca.App
{
    public class MediaConfirmationModal : BaseModal
    {
        public TextMeshProUGUI titleText;
        public MediaPreviewUI mediaPreview;
        public Button confirmButton;
        public Button cancelButton;

        private Action _onConfirm;
        private Action _onCancel;

        public void Setup(MediaInfo media, string title, Action onConfirm, Action onCancel)
        {
            if (titleText != null) titleText.text = title;
            if (mediaPreview != null && media != null) mediaPreview.LoadPreview(media);
            _onConfirm = onConfirm;
            _onCancel = onCancel;
        }

        private void Awake()
        {
            if (confirmButton != null) confirmButton.onClick.AddListener(OnConfirmClicked);
            if (cancelButton != null) cancelButton.onClick.AddListener(OnCancelClicked);
        }

        protected override void OnDestroy()
        {
            base.OnDestroy(); // untrack from ModalManager on external destroy
            if (confirmButton != null) confirmButton.onClick.RemoveListener(OnConfirmClicked);
            if (cancelButton != null) cancelButton.onClick.RemoveListener(OnCancelClicked);
        }

        private void OnConfirmClicked()
        {
            _onConfirm?.Invoke();
            Close();
        }

        private void OnCancelClicked()
        {
            _onCancel?.Invoke();
            Close();
        }

        public override void SetNoButtonVisible(bool visible)
        {
            if (cancelButton != null)
                cancelButton.gameObject.SetActive(visible);
        }
    }
} 