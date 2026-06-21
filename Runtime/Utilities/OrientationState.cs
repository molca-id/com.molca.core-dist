using System;
using UnityEngine;
using UnityEngine.Events;

public class OrientationState : MonoBehaviour
{
    [SerializeField]
    private UnityEvent<DeviceOrientation> _onOrientationChanged;
    [SerializeField]
    private UnityEvent _onOrientationPortrait;
    [SerializeField]
    private UnityEvent _onOrientationLandscape;

    private static Action<DeviceOrientation> onOrientationChanged;
    private static DeviceOrientation lastOrientation;

    private void Awake()
    {
        onOrientationChanged += (orientation) => _onOrientationChanged?.Invoke(orientation);
        lastOrientation = DeviceOrientation.Unknown;
        InvokeRepeating(nameof(CheckOrientation), 0f, 1f);
    }

    void CheckOrientation()
    {
        DeviceOrientation currentOrientation = Input.deviceOrientation;
        if (lastOrientation != currentOrientation)
            NotifyChange(currentOrientation);
    }

    void NotifyChange(DeviceOrientation newOrientation)
    {
        onOrientationChanged.Invoke(newOrientation);
        if (newOrientation == DeviceOrientation.Portrait || newOrientation == DeviceOrientation.PortraitUpsideDown)
            _onOrientationPortrait?.Invoke();
        else
            _onOrientationLandscape?.Invoke();
        lastOrientation = newOrientation;
    }

    public static void Subscribe(Action<DeviceOrientation> callback)
    {
        onOrientationChanged += callback;
        callback(lastOrientation);
    }

    public static void Unsubscribe(Action<DeviceOrientation> callback)
    {
        onOrientationChanged -= callback;
    }
}
