using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;

namespace Molca.Modals
{
    public class ModalConfirmation : BaseModal
    {
        [Header("UI Elements")]
        [SerializeField] private TextMeshProUGUI titleText;
        [SerializeField] private TextMeshProUGUI subtitleText;
        [SerializeField] private TextMeshProUGUI mainMessageText;
        [SerializeField] private ScrollRect detailsScrollRect;
        [SerializeField] private TextMeshProUGUI detailsText;
        [SerializeField] private Button yesButton;
        [SerializeField] private Button noButton;

        private Action _onYes;
        private Action _onNo;

        /// <summary>Populates all dialog fields and subscribes button listeners.</summary>
        public void Setup(string title, string subtitle, string mainMessage, string details, string yesText, string noText, Action onYes, Action onNo, bool showNoButton = true)
        {
            if (titleText != null) titleText.text = title;
            if (subtitleText != null) subtitleText.text = subtitle;
            if (mainMessageText != null) mainMessageText.text = mainMessage;
            if (detailsText != null) detailsText.text = details;

            if (yesButton != null)
            {
                var yesLabel = yesButton.GetComponentInChildren<TextMeshProUGUI>();
                if (yesLabel != null) yesLabel.text = yesText;
            }

            if (noButton != null)
            {
                var noLabel = noButton.GetComponentInChildren<TextMeshProUGUI>();
                if (noLabel != null) noLabel.text = noText;
                noButton.gameObject.SetActive(showNoButton);
            }

            _onYes = onYes;
            _onNo  = onNo;

            if (mainMessageText != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(mainMessageText.transform as RectTransform);
        }

        private void Awake()
        {
            if (yesButton != null) yesButton.onClick.AddListener(OnYesClicked);
            if (noButton != null)  noButton.onClick.AddListener(OnNoClicked);
        }

        private void OnDestroy()
        {
            if (yesButton != null) yesButton.onClick.RemoveListener(OnYesClicked);
            if (noButton != null)  noButton.onClick.RemoveListener(OnNoClicked);
        }

        private void OnYesClicked()
        {
            Close();
            _onYes?.Invoke();
        }

        private void OnNoClicked()
        {
            Close();
            _onNo?.Invoke();
        }
    }
}
