using Molca.App;
using UnityEngine;

namespace Molca.App.UI
{
    public class PanelCycle : MonoBehaviour
    {
        [SerializeField] ButtonState nextButton;
        [SerializeField] ButtonState previousButton;
        [SerializeField] bool loop = true;
        [SerializeField] GameObject[] panels;

        private int currentIndex = 0;

        // Start is called once before the first execution of Update after the MonoBehaviour is created
        void Start()
        {
            ShowCurrentPanel();
            nextButton.onClicked += OnNextClicked;
            previousButton.onClicked += OnPreviousClicked;
            UpdateButtonStates();
        }

        public void Next()
        {
            // If not looping and at the last index, disable nextButton and return
            if (!loop && currentIndex >= panels.Length - 1)
            {
                UpdateButtonStates();
                return;
            }

            currentIndex++;
            if (currentIndex >= panels.Length)
            {
                if (loop)
                {
                    currentIndex = 0;
                }
            }
            ShowCurrentPanel();
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
                    currentIndex = panels.Length - 1;
                }
            }
            ShowCurrentPanel();
            UpdateButtonStates();
        }

        /// <summary>
        /// Shows the current panel and hides all others
        /// </summary>
        private void ShowCurrentPanel()
        {
            for (int i = 0; i < panels.Length; i++)
            {
                if (panels[i] != null)
                {
                    panels[i].SetActive(i == currentIndex);
                }
            }
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
                    nextButton.isOn = currentIndex < panels.Length - 1;
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