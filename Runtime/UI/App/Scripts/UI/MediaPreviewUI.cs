using Molca.App.Media;
using UnityEngine;
using UnityEngine.UI;

namespace Molca.App.UI
{
    public class MediaPreviewUI : MonoBehaviour
    {
        [SerializeField] private GameObject loadingUI;
        [SerializeField] private GameObject failPreview;
        [SerializeField] private RawImage rawImage;
        [SerializeField] private Button openImageButton;
        [SerializeField] private AspectRatioFitter aspectRatioFitter;

        private Texture2D _cachedTexture;
        public MediaInfo MediaInfo { get; private set; }

        private void Start()
        {
            openImageButton.onClick.AddListener(OpenMedia);
        }

        private void OnDestroy()
        {
            Clear();
        }

        public async void LoadPreview(MediaInfo info, bool cacheTexture = false)
        {
            try
            {
                if (info == null)
                    failPreview.SetActive(true);

                if (MediaInfo == info)
                    return;
                Clear();

                MediaInfo = info;
                loadingUI.SetActive(true);

                Texture2D texture = await MediaInfo.GetTexture();
                if (info != MediaInfo) // Check if preview has ben refreshed with a new media info
                {
                    Debug.Log($"Abort preview operation, media missmatch, op: {info.id}, current: {MediaInfo?.id}");
                    return;
                }

                loadingUI.SetActive(false);
                if(cacheTexture)
                    _cachedTexture = CopyTexture(texture);

                if(texture)
                    SetTexture(cacheTexture ? _cachedTexture : texture);
                else
                    failPreview.SetActive(true);
            }
            catch (System.OperationCanceledException)
            {
                // cancellation is not an error — exit quietly
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }

        public void SetTexture(Texture2D texture)
        {
            rawImage.texture = texture;
            rawImage.gameObject.SetActive(true);
            aspectRatioFitter.aspectRatio = texture.width / (float)texture.height;
            openImageButton.gameObject.SetActive(true);
        }

        public void Clear()
        {
            if (MediaInfo != null)
            {
                MediaInfo.Unload();
                MediaInfo = null;
            }

            if(_cachedTexture != null)
                Destroy(_cachedTexture);

            rawImage.gameObject.SetActive(false);
            openImageButton.gameObject.SetActive(false);
            failPreview.SetActive(false);
        }
        private void OpenMedia()
        {
            if (MediaInfo != null)
            {
                switch (MediaInfo.type)
                {
                    case MediaInfo.Type.Image:
                        ImageHandler.Load(MediaInfo);
                        break;
                    case MediaInfo.Type.Video:
                        VideoHandler.Load(MediaInfo);
                        break;
                    case MediaInfo.Type.Document:
    #pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                        DocumentHandler.Load(MediaInfo);
    #pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                        break;
                    default:
                        break;
                }
            }
            else if (rawImage.texture != null)
            {
                ImageHandler.Load(rawImage.texture);
            }
        }

        private Texture2D CopyTexture(Texture2D sourceTexture)
        {
            // Create a new texture with the same dimensions and format
            Texture2D newTexture = new Texture2D(sourceTexture.width, sourceTexture.height,
                sourceTexture.format, false);

            // Direct copy of texture
            Graphics.CopyTexture(sourceTexture, 0, 0, newTexture, 0, 0);

            return newTexture;
        }
    }
}