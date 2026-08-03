using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Molca.App.UI
{
    public class BatteryStatus : MonoBehaviour
    {
        [SerializeField]
        private TextMeshProUGUI percentageText;
        [SerializeField]
        private Image bateryFill;

        private void OnEnable()
        {
            InvokeRepeating(nameof(UpdateStatus), 0f, 4f);
        }

        private void OnDisable()
        {
            CancelInvoke();
        }

        private void UpdateStatus()
        {
            float battery = SystemInfo.batteryLevel;
            percentageText.text = string.Format("{0:0}%", Mathf.RoundToInt(battery * 100));
            bateryFill.fillAmount = battery;
        }
    }
}