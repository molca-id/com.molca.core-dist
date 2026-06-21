using UnityEngine;
using UnityEngine.Events;

public class EventArray : MonoBehaviour
{
    [SerializeField, TextArea]
    private string note;
    public UnityEvent events;

    public void Invoke()
    {
        events?.Invoke();
    }
}