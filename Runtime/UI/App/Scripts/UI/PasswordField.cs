using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Molca.App.UI
{
    [RequireComponent(typeof(TMP_InputField))]
    public class PasswordField : MonoBehaviour
    {
        [SerializeField]
        private Image toggleImage;
        [SerializeField]
        private Sprite showSprite;
        [SerializeField]
        private Sprite hideSprite;

        private TMP_InputField _inputField;
        private bool _isVisible = false;

        private void Start()
        {
            _inputField = GetComponent<TMP_InputField>();
        }

        public void ToggleVisibility()
        {
            _isVisible = !_isVisible;
            toggleImage.sprite = _isVisible ? showSprite : hideSprite;
            _inputField.contentType = _isVisible ? TMP_InputField.ContentType.Standard : TMP_InputField.ContentType.Password;
            _inputField.ForceLabelUpdate();
        }
    }
}
