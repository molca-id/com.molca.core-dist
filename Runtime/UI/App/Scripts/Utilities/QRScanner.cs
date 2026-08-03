using System.Collections;
using System;
using UnityEngine;
using UnityEngine.UI;
using ZXing;
using UnityEngine.Events;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif
using Molca.Attributes;
using Molca.Modals;

namespace Molca.App.Utilities
{
    public class QRScanner : MonoBehaviour
    {
        [SerializeField]
        private RectTransform root;
        [SerializeField]
        private RectTransform viewerTransform;

        [SerializeField]
        private ModalConfirmationHelper errorConfirmation;

        private WebCamTexture _webcamTexture;
        [SerializeField, ReadOnly]
        private string _qrCode = string.Empty;

        private string _deviceName;
        private bool _isReady;

        public UnityEvent<string> onScanned;

        IEnumerator Start()
        {
            // Request camera permission on Android/iOS
    #if PLATFORM_ANDROID
            yield return new WaitForEndOfFrame();
            if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
            {
                Permission.RequestUserPermission(Permission.Camera);
            }
    #elif PLATFORM_IOS
            yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
    #else
            yield return null;
    #endif

            onScanned.AddListener((txt) =>
            {
                Debug.Log($"DECODED TEXT FROM QR: {txt}");
                gameObject.SetActive(false);
            });

            WebCamDevice[] devices = WebCamTexture.devices;
            // Create a WebCamTexture using the first available camera
            _webcamTexture = new WebCamTexture(devices[0].name);

            // Start the camera
            _webcamTexture.Play();

            // Wait for the camera to start
            while (_webcamTexture.width <= 16)
            {
                yield return new WaitForEndOfFrame();
            }

            viewerTransform.GetComponent<AspectRatioFitter>().aspectRatio = _webcamTexture.width / (float)_webcamTexture.height;
            viewerTransform.localEulerAngles = new Vector3(0, 0, -_webcamTexture.videoRotationAngle);
            _isReady = true;
        }

        private void OnEnable()
        {
            StartCoroutine(InitializeCamera());
        }

        private void OnDisable()
        {
            if (_webcamTexture != null) 
                Destroy(_webcamTexture);
        }

        private IEnumerator InitializeCamera()
        {
            while (!_isReady) yield return new WaitForEndOfFrame();

            // Check if we have permission
            if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
            {
                errorConfirmation.Create();
                yield break;
            }

            _qrCode = string.Empty;
            if (_webcamTexture == null)
            {
                _webcamTexture = new WebCamTexture(_deviceName);
                _webcamTexture.Play();
            }
            viewerTransform.GetComponent<RawImage>().texture = _webcamTexture;

            StartCoroutine(GetQRCode());
        }

        IEnumerator GetQRCode()
        {
            IBarcodeReader barCodeReader = new BarcodeReader();
            var snap = new Texture2D(_webcamTexture.width, _webcamTexture.height, TextureFormat.ARGB32, false);
            while (string.IsNullOrEmpty(_qrCode))
            {
                try
                {
                    snap.SetPixels32(_webcamTexture.GetPixels32());
                    var Result = barCodeReader.Decode(snap.GetRawTextureData(), _webcamTexture.width, _webcamTexture.height, RGBLuminanceSource.BitmapFormat.ARGB32);
                    if (Result != null)
                    {
                        _qrCode = Result.Text;
                    }
                }
                catch (Exception ex) { Debug.LogWarning(ex.Message); }
                yield return null;
            }
            _webcamTexture.Stop();
            onScanned?.Invoke(_qrCode);
        }

        private void OnGUI()
        {
            int w = Screen.width, h = Screen.height;

            GUIStyle style = new GUIStyle();

            Rect rect = new Rect(0, 0, w, h * 2 / 100);
            style.alignment = TextAnchor.UpperLeft;
            style.fontSize = h * 2 / 50;
            style.normal.textColor = new Color(0.0f, 0.0f, 0.5f, 1.0f);
            string text =_qrCode;
            GUI.Label(rect, text, style);
        }
    }
}