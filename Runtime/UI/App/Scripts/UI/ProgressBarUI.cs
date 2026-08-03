using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;
using UnityEngine.Events;

namespace Molca.App.UI
{
    public class ProgressBarUI : MonoBehaviour
    {
        [SerializeField]
        private TextMeshProUGUI title;
        [SerializeField]
        private Slider progressBar;
        [SerializeField]
        private float updateInterval;

        private float _lastUpdate = 0.01f;
        private bool _isDragging;
        private bool _filled;

        public Action onBeginDrag;
        public Action onEndDrag;
        public Action onProgressFilled;

        public string Title
        {
            get => title.text;
            set => title.text = value;
        }

        public float Progress
        {
            get => progressBar.value;
            private set => progressBar.value = value;
        }

        public UnityEvent<float> onValueChanged;

        public void OnSliderDragStart()
        {
            _isDragging = true;
            onBeginDrag?.Invoke();
        }

        public void OnSliderDragEnd()
        {
            _isDragging = false;
            onEndDrag?.Invoke();
            onValueChanged?.Invoke(Progress);
        }

        public void UpdateProgress(float progress)
        {
            if (_isDragging) return;

            progress = Mathf.Clamp01(progress);
            if (Progress == progress)
                return;

            //Debug.Log($"Update progress: {progress}");
            Progress = progress;

            if (progress < 1f)
            {
                if (_filled) _filled = false;
                return;
            }

            if (_filled) return;

            _filled = true;
            onProgressFilled?.Invoke();
        }

        private void Update()
        {
            if(!_isDragging || Time.time - _lastUpdate < updateInterval) return;

            onValueChanged?.Invoke(Progress);
            _lastUpdate = Time.time;
        }
    }
}