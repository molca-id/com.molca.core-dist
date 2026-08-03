using Molca.App.Media;
using UnityEngine;

namespace Molca.App.UI
{
    public class MediaInfoCycle : MonoBehaviour
    {
        [SerializeField] MediaPreviewUI mediaPreview;
        [SerializeField] ButtonState nextButton;
        [SerializeField] ButtonState previousButton;
        [SerializeField] bool loop = true;
        [SerializeField] MediaInfo[] mediaInfos;

        private int currentIndex = 0;

        // Start is called once before the first execution of Update after the MonoBehaviour is created
        void Start()
        {
            mediaPreview.LoadPreview(mediaInfos[currentIndex]);
            if (nextButton != null)
                nextButton.onClicked += OnNextClicked;
            if (previousButton != null)
                previousButton.onClicked += OnPreviousClicked;
            UpdateButtonStates();
        }

        public void Next()
        {
            // If not looping and at the last index, disable nextButton and return
            if (!loop && currentIndex >= mediaInfos.Length - 1)
            {
                UpdateButtonStates();
                return;
            }

            currentIndex++;
            if (currentIndex >= mediaInfos.Length)
            {
                if (loop)
                {
                    currentIndex = 0;
                }
            }
            mediaPreview.LoadPreview(mediaInfos[currentIndex]);
            UpdateButtonStates();
        }

        public void Previous()
        {
            // If not looping and at the first index, disable previousButton and return
            if (!loop && currentIndex <= 0)
            {
                UpdateButtonStates();
                return;
            }

            currentIndex--;
            if (currentIndex < 0)
            {
                if (loop)
                {
                    currentIndex = mediaInfos.Length - 1;
                }
            }
            mediaPreview.LoadPreview(mediaInfos[currentIndex]);
            UpdateButtonStates();
        }

        /// <summary>
        /// Updates the interactable state of next and previous buttons based on current index and loop setting
        /// </summary>
        private void UpdateButtonStates()
        {
            if (loop)
            {
                // When looping, buttons are always enabled
                if (nextButton != null)
                    nextButton.isOn = true;
                if (previousButton != null)
                    previousButton.isOn = true;
            }
            else
            {
                // When not looping, disable buttons at boundaries
                if (nextButton != null)
                    nextButton.isOn = currentIndex < mediaInfos.Length - 1;
                if (previousButton != null)
                    previousButton.isOn = currentIndex > 0;
            }
        }

        private void OnNextClicked(ButtonState buttonState)
        {
            Next();
        }

        private void OnPreviousClicked(ButtonState buttonState)
        {
            Previous();
        }
    }
}