using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Molca.Modals
{
    public class ModalLoading : MonoBehaviour
    {
        public string Title { get; private set; }

        [SerializeField] private TextMeshProUGUI messageText;
        [SerializeField] private Image loadingBar;
        [SerializeField] private float smoothSpeed = 10f;

        private float _targetProgress;
        private float _currentProgress;

        private void Update()
        {
            if (Mathf.Approximately(_currentProgress, _targetProgress)) return;
            
            _currentProgress = Mathf.Lerp(_currentProgress, _targetProgress, Time.deltaTime * smoothSpeed);
            loadingBar.fillAmount = _currentProgress;
        }

        public void Initialize(string title)
        {
            Title = title;
            messageText.SetText(title);
            _currentProgress = _targetProgress = 0;
            loadingBar.fillAmount = 0;
        }

        public void Refresh(string msg, float progress)
        {
            messageText.SetText(msg);
            _targetProgress = Mathf.Clamp01(progress);
        }
    }
}