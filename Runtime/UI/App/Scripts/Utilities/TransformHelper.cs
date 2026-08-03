using UnityEngine;

public class TransformHelper : MonoBehaviour
{
    [SerializeField] private bool resetOnEnable = false;

    private Vector3 _originalPosition;
    private Quaternion _originalRotation;
    private Vector3 _originalScale;

    private bool _isInitialized = false;

    private void OnEnable()
    {
        if(resetOnEnable)
        {
            ResetPosition();
            ResetRotation();
            ResetScale();
        }
    }

    private void Awake()
    {
        _originalPosition = transform.position;
        _originalRotation = transform.rotation;
        _originalScale = transform.localScale;
        _isInitialized = true;
    }

    public void ResetPosition()
    {
        if(!_isInitialized) return;
        transform.position = _originalPosition;
    }

    public void ResetRotation()
    {
        if(!_isInitialized) return;
        transform.rotation = _originalRotation;
    }

    public void ResetScale()
    {
        if(!_isInitialized) return;
        transform.localScale = _originalScale;
    }
}
