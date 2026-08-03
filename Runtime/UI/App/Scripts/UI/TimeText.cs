using System;
using System.Globalization;
using UnityEngine;
using TMPro;

[RequireComponent(typeof(TextMeshProUGUI))]
public class TimeText : MonoBehaviour
{
    [SerializeField]
    private bool showDate;

    private TextMeshProUGUI _text;

    private void Start()
    {
        _text = GetComponent<TextMeshProUGUI>();
    }

    private void OnEnable()
    {
        InvokeRepeating(nameof(UpdateTime), 0f, 1f);
    }

    private void OnDisable()
    {
        CancelInvoke();
    }

    private void UpdateTime()
    {
        _text.text = $"{DateTime.Now.ToString("hh:mm")}";
        if(showDate)
            _text.text = $"{_text.text}   {DateTime.Now.ToString("M", CultureInfo.CurrentCulture)}";
    }
}
