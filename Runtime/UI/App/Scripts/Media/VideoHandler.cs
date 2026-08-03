using System.Collections;
using UnityEngine;
using UnityEngine.Video;
using Molca;
using Molca.App;
using UnityEngine.UI;
using TMPro;
using Molca.App.UI;

namespace Molca.App.Media
{
    public class VideoHandler : MonoBehaviour
    {
        private static VideoHandler _instance;

        [SerializeField]
        private TextMeshProUGUI title;
        [SerializeField] private VideoPlayer player;
        [SerializeField]
        private RectTransform imageContainer;
        [SerializeField]
        private RawImage targetImage;
        [SerializeField]
        private AspectRatioFitter backgroundRatioFitter;
        [SerializeField]
        private CanvasGroup canvasGroup;

        [Header("Visibility")]
        [SerializeField] private Button closeButton;
        [SerializeField] private GameObject rootPanel;
        //[SerializeField] private Image rootBackground;

        [Header("Playback Control")]
        [SerializeField] private ButtonState playButtonState;
        [SerializeField] private Button stopButton;
        [SerializeField] private ProgressBarUI playbackBar;

        private float _lastRefresh;
        private bool _isPlayingInternal;
        private static bool IsPlaying => _instance.player.isPlaying;

        private void Awake()
        {
            if (_instance != null)
            {
                Debug.Log($"{typeof(VideoHandler)} already exist: {_instance}");
                Destroy(gameObject);
                return;
            }
            _instance = this;
            playbackBar.UpdateProgress(0f);
            closeButton.onClick.AddListener(CloseViewer);
            playButtonState.onStateChanged.AddListener(TogglePlayState);
            playbackBar.onValueChanged.AddListener(SetVideoProgress);
            playbackBar.onBeginDrag += () =>
            {
                if (_isPlayingInternal) player.Pause();
            };
            playbackBar.onEndDrag += () =>
            {
                if (_isPlayingInternal) player.Play();
            };
            stopButton.onClick.AddListener(() =>
            {
                if (!player.isPrepared) return;
                playButtonState.isOn = false;
                player.Stop();
                playbackBar.UpdateProgress(0f);
            });

            player.prepareCompleted += OnPreparationCompleted;

            CloseViewer();
        }

        private void LateUpdate()
        {
            if (Time.time - _lastRefresh < .05f)
                return;

            RefreshBar();
            _lastRefresh = Time.time;
        }

        private void OnPreparationCompleted(VideoPlayer source)
        {
            player.time = 0;
            playButtonState.isOn = true; // this will call TogglePlayState
            targetImage.GetComponent<AspectRatioFitter>().aspectRatio = player.width / (float)player.height;
            backgroundRatioFitter.aspectRatio = player.width / (player.height * 1.05f);
        }

        public static async Awaitable<Texture2D> GetThumbnail(MediaInfo mediaInfo)
        {
            Debug.Log($"Getting video thumbnail of media: {mediaInfo.name}");

            // enable video player but make it invisible
            _instance.canvasGroup.enabled = true;
            _instance.ToggleVisibility(true);

            var tempRenderTexture = new RenderTexture(_instance.player.targetTexture);
            var tempPlayer = new GameObject("GetThumbnail").AddComponent<VideoPlayer>();
            tempPlayer.renderMode = VideoRenderMode.RenderTexture;
            tempPlayer.targetTexture = tempRenderTexture;
            tempPlayer.audioOutputMode = VideoAudioOutputMode.None;
            if (!await mediaInfo.PrepareVideo(tempPlayer))
            {
                Debug.LogWarning("Failed to prepare video.");
                _instance.ToggleVisibility(false);
                _instance.canvasGroup.enabled = false;
                return null;
            }

            tempPlayer.Play();
            while(!tempPlayer.isPlaying)
                await Awaitable.NextFrameAsync();

            Texture2D frameTexture = null;
            while(frameTexture == null || !tempRenderTexture.IsCreated())
            {
                Debug.Log($"Getting thumbnail for media: {mediaInfo.name}");
                await Awaitable.WaitForSecondsAsync(.1f);
                tempPlayer.Pause(); // Pause the video to capture the frame
                await Awaitable.NextFrameAsync();

                frameTexture = new Texture2D(tempRenderTexture.width, tempRenderTexture.height, TextureFormat.RGBA32, false);

                // Read pixels from the RenderTexture
                RenderTexture.active = tempRenderTexture;
                frameTexture.ReadPixels(new Rect(0, 0, tempRenderTexture.width, tempRenderTexture.height), 0, 0);
                frameTexture.Apply();
            }
            RenderTexture.active = null;

            tempPlayer.Stop();
            Destroy(tempRenderTexture);
            Destroy(tempPlayer.gameObject);

            _instance.ToggleVisibility(false);
            _instance.canvasGroup.enabled = false;

            return frameTexture;
        }

        private void RefreshBar()
        {
            if (!IsPlaying) return;
            playbackBar.UpdateProgress((float)(player.time / player.length));
        }

        public void TogglePlayState(bool state)
        {
            _isPlayingInternal = state;
            if (state)
            {
                if (!player.isPrepared) player.Prepare();
                else player.Play();
            } 
            else player.Pause();
        }

        public void SetVideoProgress(float progress)
        {
            if (!player.isPrepared) return;
            player.time = player.length * progress;
            _lastRefresh = Time.time;
        }

        public static async void Load(MediaInfo info)
        {
            try
            {
                _instance.ToggleVisibility(true);
                _instance.title.text = string.IsNullOrWhiteSpace(info.name) ? "Video Player" : info.name;
                if (!await info.PrepareVideo(_instance.player))
                    Debug.LogError($"Failed to load video from media info {info}");
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

        public static void Load(string url, string title = null)
        {
            _instance.ToggleVisibility(true);
            _instance.title.text = string.IsNullOrWhiteSpace(title) ? "Video Player" : title;

            _instance.player.source = VideoSource.Url;
            _instance.player.url = url;
            _instance.player.Prepare();
        }

        public void CloseViewer()
        {
            player.Stop();
            player.clip = null;
            player.source = VideoSource.VideoClip;
            player.url = string.Empty;
            ToggleVisibility(false);
        }

        private void ToggleVisibility(bool visible)
        {
            //rootBackground.enabled = visible;
            rootPanel.SetActive(visible);
        }

        /// <summary>
        /// Auto close viewer if application loses focus
        /// </summary>
        /// <param name="focus"></param>
        private void OnApplicationFocus(bool focus)
        {
            if (focus) return;
            CloseViewer();
        }
    }
}
