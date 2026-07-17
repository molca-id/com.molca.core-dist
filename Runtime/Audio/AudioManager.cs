using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Audio;
using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Settings;
using Molca.Events;
using Molca.Localization;

namespace Molca.Audio
{
    public class AudioManager : RuntimeSubsystem
    {
        [Header("Audio Sources")]
        [SerializeField, FormerlySerializedAs("musicSource")] private AudioSource _musicSource;
        [SerializeField, FormerlySerializedAs("sfxSource")] private AudioSource _sfxSource;
        [SerializeField, FormerlySerializedAs("voiceSource")] private AudioSource _voiceSource;

        [Header("Voice Ducking")]
        [SerializeField, FormerlySerializedAs("duckingAmount")] private float _duckingAmount = 0.5f;
        [SerializeField, FormerlySerializedAs("duckingFadeTime")] private float _duckingFadeTime = 0.3f;

        [Header("Music Fading")]
        [SerializeField, FormerlySerializedAs("musicFadeDuration")] private float _musicFadeDuration = 1f;

        private AudioModule _audioSettings;
        private bool _isDucking = false;
        // AudioSource volumes captured when ducking starts. Restores MUST use these:
        // the SettingModule volume getters live in the mixer-dB domain, and writing
        // those onto AudioSource.volume double-attenuates (source x mixer) forever.
        private float _preDuckMusicVolume = 1f;
        private float _preDuckSfxVolume = 1f;

        // Tracks every loaded clip by its full identity. RefCount is logical:
        // the underlying Addressables handle is acquired once per key (on first
        // load) and released once, when the count returns to zero. This lets
        // overlapping plays of the same clip (e.g., rapid SFX) share one load
        // without the first release cutting off the later playback.
        private readonly Dictionary<(string collection, string id, AudioLibrary.AudioType type), LoadedClipRecord> _loadedClips
            = new Dictionary<(string, string, AudioLibrary.AudioType), LoadedClipRecord>();

        private (string collection, string id, AudioLibrary.AudioType type)? _currentMusicKey;
        private (string collection, string id, AudioLibrary.AudioType type)? _currentVoiceKey;
        private AudioClip _currentMusicClip;
        private AudioClip _currentVoiceClip;
        private Coroutine _currentVoiceCoroutine;
        // The active music play/fade coroutine. Tracked so a second PlayMusic can
        // stop the previous one instead of two coroutines fighting over the source.
        private Coroutine _currentMusicCoroutine;
        // The previous track the active music coroutine still owes a release for
        // (released after its fade-out, or eagerly if the coroutine is stopped).
        private (string collection, string id, AudioLibrary.AudioType type)? _pendingMusicRelease;

        private sealed class LoadedClipRecord
        {
            public AudioClip Clip;
            public int RefCount;
        }

        // Backing field kept so internal lifecycle code avoids the obsolete accessor.
        private static AudioManager _instance;

        [Obsolete("Use RuntimeManager.GetSubsystem<AudioManager>() or [Inject] AudioManager.")]
        public static AudioManager Instance => _instance;
        public AudioLibrary MusicLibrary => _audioSettings != null ? _audioSettings.MusicLibrary : null;
        public AudioLibrary SFXLibrary => _audioSettings != null ? _audioSettings.SFXLibrary : null;
        public AudioLibrary VoiceLibrary => _audioSettings != null ? _audioSettings.VoiceLibrary : null;

        public float MasterVolume => _audioSettings.MasterVolume;
        public float MusicVolume => _audioSettings.MusicVolume;
        public float SFXVolume => _audioSettings.SFXVolume;
        public float VoiceVolume => _audioSettings.VoiceVolume;

        public override void Initialize(Action<IRuntimeSubsystem> finishCallback)
        {
            if (_instance != null)
            {
                Debug.LogWarning("Multiple AudioManager instances detected. Destroying duplicate.");
                Destroy(gameObject);
                // Still signal completion — bootstrap blocks until every subsystem
                // invokes its finishCallback.
                finishCallback?.Invoke(this);
                return;
            }

            _instance = this;

            // Get audio settings from GlobalSettings
            _audioSettings = GlobalSettings.GetModule<AudioModule>();
            if (_audioSettings == null)
            {
                Debug.LogError("AudioModule not found in GlobalSettings!");
                // Signal completion even on configuration error so bootstrap can proceed.
                finishCallback?.Invoke(this);
                return;
            }

            // Initialize audio sources if not set
            if (_musicSource == null) _musicSource = gameObject.AddComponent<AudioSource>();
            if (_sfxSource == null) _sfxSource = gameObject.AddComponent<AudioSource>();
            if (_voiceSource == null) _voiceSource = gameObject.AddComponent<AudioSource>();

            // Set up audio sources with mixer groups
            if (_audioSettings.audioMixerBGM != null) _musicSource.outputAudioMixerGroup = _audioSettings.audioMixerBGM;
            if (_audioSettings.audioMixerSFX != null) _sfxSource.outputAudioMixerGroup = _audioSettings.audioMixerSFX;
            if (_audioSettings.audioMixerVO != null) _voiceSource.outputAudioMixerGroup = _audioSettings.audioMixerVO;

            // Initialize libraries
            if (MusicLibrary != null) MusicLibrary.Initialize();
            if (SFXLibrary != null) SFXLibrary.Initialize();
            if (VoiceLibrary != null) VoiceLibrary.Initialize();

            finishCallback?.Invoke(this);
        }

        public override void Teardown()
        {
            base.Teardown();
            StopMusic();
            StopVoice();

            if (MusicLibrary != null) MusicLibrary.Clear();
            if (SFXLibrary != null) SFXLibrary.Clear();
            if (VoiceLibrary != null) VoiceLibrary.Clear();
        }

        private AudioLibrary GetLibrary(AudioLibrary.AudioType type) => type switch
        {
            AudioLibrary.AudioType.Music => MusicLibrary,
            AudioLibrary.AudioType.SFX => SFXLibrary,
            AudioLibrary.AudioType.Voice => VoiceLibrary,
            _ => null
        };

        private async Awaitable<AudioClip> LoadAudioClip(string collectionName, string id, AudioLibrary.AudioType type)
        {
            var key = (collectionName, id, type);
            if (_loadedClips.TryGetValue(key, out var record))
            {
                record.RefCount++;
                return record.Clip;
            }

            var library = GetLibrary(type);
            if (library == null)
            {
                Debug.LogError($"Library for type {type} not found!");
                return null;
            }

            AudioClip clip;
            if (type == AudioLibrary.AudioType.Voice)
            {
                var localizedEntry = library.GetLocalizedEntry(collectionName, id);
                if (localizedEntry == null)
                {
                    Debug.LogWarning($"Voice audio entry '{id}' not found in collection '{collectionName}'!");
                    return null;
                }
                clip = await localizedEntry.GetLocalizedClip();
            }
            else
            {
                var entry = library.GetEntry(collectionName, id);
                if (entry == null)
                {
                    Debug.LogWarning($"Audio entry '{id}' not found in collection '{collectionName}'!");
                    return null;
                }
                clip = await entry.GetClip();
            }

            if (clip != null)
            {
                // A concurrent load for the same key may have finished while we awaited.
                if (_loadedClips.TryGetValue(key, out var existing))
                {
                    existing.RefCount++;
                    return existing.Clip;
                }
                _loadedClips[key] = new LoadedClipRecord { Clip = clip, RefCount = 1 };
            }
            return clip;
        }

        private void ReleaseAudioClip((string collection, string id, AudioLibrary.AudioType type) key)
        {
            if (!_loadedClips.TryGetValue(key, out var record))
                return;

            record.RefCount--;
            if (record.RefCount > 0)
                return;

            _loadedClips.Remove(key);
            ReleaseLibraryAsset(key);
        }

        private void ReleaseLibraryAsset((string collection, string id, AudioLibrary.AudioType type) key)
        {
            // Release through the library the clip was actually loaded from —
            // never guess by null-coalescing across libraries.
            var library = GetLibrary(key.type);
            if (library == null) return;

            if (key.type == AudioLibrary.AudioType.Voice)
            {
                var entry = library.GetLocalizedEntry(key.collection, key.id);
                entry?.ReleaseAsset();
            }
            else
            {
                var entry = library.GetEntry(key.collection, key.id);
                entry?.clipReference.ReleaseAsset();
            }
        }

        #region Public API

        // Music Controls

        /// <summary>
        /// Legacy entry point for <see cref="PlayMusicAsync"/>. Thin async-void shim:
        /// exceptions are contained here instead of escaping into Unity's
        /// synchronization context unobserved.
        /// </summary>
        public async void PlayMusic(string collectionName, string id)
        {
            try
            {
                await PlayMusicAsync(collectionName, id);
            }
            catch (OperationCanceledException)
            {
                // Shutdown/caller cancel — not an error.
            }
            catch (Exception e)
            {
                Debug.LogError($"[AudioManager] PlayMusic('{collectionName}', '{id}') failed: {e}");
            }
        }

        /// <summary>
        /// Loads (or reuses) the clip and starts music playback with a cross-fade.
        /// Completes once playback has been handed to the fade coroutine.
        /// </summary>
        /// <param name="collectionName">The music collection to look the clip up in.</param>
        /// <param name="id">The clip id inside the collection.</param>
        /// <param name="cancellationToken">
        /// Aborts before playback starts (the clip ref taken by the load is balanced).
        /// Linked with the subsystem's <c>ShutdownToken</c>.
        /// </param>
        public async Awaitable PlayMusicAsync(string collectionName, string id, System.Threading.CancellationToken cancellationToken = default)
        {
            var key = (collectionName, id, AudioLibrary.AudioType.Music);
            var clip = await LoadAudioClip(collectionName, id, AudioLibrary.AudioType.Music);
            if (clip == null) return;

            // Post-await checks: teardown/destroy/caller-cancel while loading.
            if (this == null || ShutdownToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                ReleaseAudioClip(key); // balance the load's ref before bailing
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            // A previous play/fade coroutine still running would fight this one over
            // the music source (and could release the wrong track) — stop it and
            // settle its pending release first.
            if (_currentMusicCoroutine != null)
            {
                StopCoroutine(_currentMusicCoroutine);
                _currentMusicCoroutine = null;
                ReleasePendingMusicTrack();
            }

            if (_currentMusicKey == key)
            {
                // Same track requested again — balance the extra ref taken by the
                // load above and just restart the fade-in.
                ReleaseAudioClip(key);
                _currentMusicCoroutine = StartCoroutine(PlayMusicCoroutine(clip));
                return;
            }

            // The previous clip is still fading out inside PlayMusicCoroutine;
            // it is released there, after the fade completes.
            _pendingMusicRelease = _currentMusicKey;
            _currentMusicKey = key;
            _currentMusicClip = clip;
            _currentMusicCoroutine = StartCoroutine(PlayMusicCoroutine(clip));
        }

        // Releases the track a stopped/finished music coroutine still owed. Idempotent.
        private void ReleasePendingMusicTrack()
        {
            if (_pendingMusicRelease != null)
            {
                ReleaseAudioClip(_pendingMusicRelease.Value);
                _pendingMusicRelease = null;
            }
        }

        public void StopMusic()
        {
            if (_currentMusicKey == null) return;

            // Kill any in-progress play/fade coroutine and settle the track it owed.
            if (_currentMusicCoroutine != null)
            {
                StopCoroutine(_currentMusicCoroutine);
                _currentMusicCoroutine = null;
            }
            ReleasePendingMusicTrack();

            var key = _currentMusicKey.Value;
            _currentMusicKey = null;
            _currentMusicClip = null;

            if (isActiveAndEnabled)
            {
                // Release only after the fade-out has finished using the clip.
                StartCoroutine(StopMusicAndReleaseCoroutine(_musicFadeDuration, key));
            }
            else
            {
                // Destroyed/disabled (e.g., Teardown) — no coroutine possible; stop and release now.
                if (_musicSource != null) _musicSource.Stop();
                ReleaseAudioClip(key);
            }
        }

        public void StopVoice()
        {
            StopCurrentVoice();
        }

        // SFX Controls

        /// <summary>
        /// Legacy entry point for <see cref="PlaySFXAsync"/>. Thin async-void shim;
        /// exceptions are contained here.
        /// </summary>
        public async void PlaySFX(string collectionName, string id, float volume = 1f, AudioSource audioSource = null)
        {
            try
            {
                await PlaySFXAsync(collectionName, id, volume, audioSource);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Debug.LogError($"[AudioManager] PlaySFX('{collectionName}', '{id}') failed: {e}");
            }
        }

        /// <summary>
        /// Loads (or reuses) the clip and plays it as a one-shot SFX. Completes once
        /// playback has started (not when the clip finishes).
        /// </summary>
        /// <param name="collectionName">The SFX collection to look the clip up in.</param>
        /// <param name="id">The clip id inside the collection.</param>
        /// <param name="volume">One-shot volume scale.</param>
        /// <param name="audioSource">Optional source to play on instead of the shared SFX source.</param>
        /// <param name="cancellationToken">Aborts before playback starts. Linked with <c>ShutdownToken</c>.</param>
        public async Awaitable PlaySFXAsync(string collectionName, string id, float volume = 1f, AudioSource audioSource = null, System.Threading.CancellationToken cancellationToken = default)
        {
            var key = (collectionName, id, AudioLibrary.AudioType.SFX);
            var clip = await LoadAudioClip(collectionName, id, AudioLibrary.AudioType.SFX);
            if (clip == null) return;

            if (this == null || ShutdownToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                ReleaseAudioClip(key);
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            if (audioSource == null)
                _sfxSource.PlayOneShot(clip, volume);
            else
            {
                audioSource.outputAudioMixerGroup = _audioSettings.audioMixerSFX;
                audioSource.PlayOneShot(clip, volume);
            }

            // Each play holds its own ref; the asset is released once the last
            // overlapping playback has finished.
            StartCoroutine(ReleaseAfterDelay(key, clip.length));
        }

        // Voice Controls

        /// <summary>
        /// Legacy entry point for <see cref="PlayVoiceAsync"/>. Thin async-void shim;
        /// exceptions are contained here.
        /// </summary>
        public async void PlayVoice(string collectionName, string id, float volume = 1f, AudioSource audioSource = null)
        {
            try
            {
                await PlayVoiceAsync(collectionName, id, volume, audioSource);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Debug.LogError($"[AudioManager] PlayVoice('{collectionName}', '{id}') failed: {e}");
            }
        }

        /// <summary>
        /// Loads the localized voice clip and plays it with music/SFX ducking,
        /// replacing any voice line already playing. Completes once playback has been
        /// handed to the ducking coroutine.
        /// </summary>
        /// <param name="collectionName">The dialog collection to look the clip up in.</param>
        /// <param name="id">The voice line id inside the collection.</param>
        /// <param name="volume">One-shot volume scale.</param>
        /// <param name="audioSource">Optional source to play on instead of the shared voice source.</param>
        /// <param name="cancellationToken">Aborts before playback starts. Linked with <c>ShutdownToken</c>.</param>
        public async Awaitable PlayVoiceAsync(string collectionName, string id, float volume = 1f, AudioSource audioSource = null, System.Threading.CancellationToken cancellationToken = default)
        {
            var key = (collectionName, id, AudioLibrary.AudioType.Voice);
            var clip = await LoadAudioClip(collectionName, id, AudioLibrary.AudioType.Voice);
            if (clip == null)
            {
                Debug.LogError($"Voice audio entry '{id}' not found in collection '{collectionName}'!");
                return;
            }

            if (this == null || ShutdownToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                ReleaseAudioClip(key);
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            // Stop any currently playing voice audio (releases its clip).
            StopCurrentVoice();

            _currentVoiceClip = clip;
            _currentVoiceKey = key;
            // The clip is released when the ducking coroutine finishes the restore
            // fade, or by StopCurrentVoice if interrupted — single owner, no timer.
            _currentVoiceCoroutine = StartCoroutine(PlayVoiceWithDuckingCoroutine(clip, volume, audioSource));
        }

        /// <summary>
        /// Legacy entry point for <see cref="PlayFromSourceAsync"/>. Thin async-void
        /// shim; exceptions are contained here.
        /// </summary>
        public async void PlayFromSource(AudioSource audioSource, string collectionName, string id, AudioLibrary.AudioType audioType, float volume = 1f)
        {
            try
            {
                await PlayFromSourceAsync(audioSource, collectionName, id, audioType, volume);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Debug.LogError($"[AudioManager] PlayFromSource('{collectionName}', '{id}') failed: {e}");
            }
        }

        /// <summary>
        /// Loads (or reuses) the clip and plays it on a caller-provided source, routed
        /// through the mixer group for <paramref name="audioType"/>. Completes once
        /// playback has started.
        /// </summary>
        /// <param name="audioSource">The source to play on. Required.</param>
        /// <param name="collectionName">The collection to look the clip up in.</param>
        /// <param name="id">The clip id inside the collection.</param>
        /// <param name="audioType">Which library/mixer group the clip belongs to.</param>
        /// <param name="volume">Source volume.</param>
        /// <param name="cancellationToken">Aborts before playback starts. Linked with <c>ShutdownToken</c>.</param>
        public async Awaitable PlayFromSourceAsync(AudioSource audioSource, string collectionName, string id, AudioLibrary.AudioType audioType, float volume = 1f, System.Threading.CancellationToken cancellationToken = default)
        {
            var key = (collectionName, id, audioType);
            var clip = await LoadAudioClip(collectionName, id, audioType);
            if (clip == null) return;

            if (this == null || audioSource == null || ShutdownToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                ReleaseAudioClip(key);
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            // Stop current voice if playing new voice audio
            if (audioType == AudioLibrary.AudioType.Voice)
            {
                StopCurrentVoice();
            }

            audioSource.outputAudioMixerGroup = audioType switch
            {
                AudioLibrary.AudioType.Music => _audioSettings.audioMixerBGM,
                AudioLibrary.AudioType.SFX => _audioSettings.audioMixerSFX,
                AudioLibrary.AudioType.Voice => _audioSettings.audioMixerVO,
                _ => _audioSettings.audioMixerMaster
            };
            audioSource.clip = clip;
            audioSource.volume = volume;
            audioSource.Play();

            // One-shot types release after playback; Music on an external source
            // may loop, so its ref is held until OnDestroy's sweep.
            if (audioType != AudioLibrary.AudioType.Music)
            {
                StartCoroutine(ReleaseAfterDelay(key, clip.length));
            }
        }

        public void SetupAudioSource(AudioSource audioSource, AudioLibrary.AudioType audioType)
        {
            audioSource.outputAudioMixerGroup = audioType switch
            {
                AudioLibrary.AudioType.Music => _audioSettings.audioMixerBGM,
                AudioLibrary.AudioType.SFX => _audioSettings.audioMixerSFX,
                AudioLibrary.AudioType.Voice => _audioSettings.audioMixerVO,
                _ => _audioSettings.audioMixerMaster
            };
        }
        
        // Volume Controls - These now delegate to AudioModule
        public void SetMasterVolume(float volume)
        {
            _audioSettings.SetMasterVolume(volume);
            TypedEvents.MasterVolumeChanged.Dispatch(volume);
        }
        
        public void SetMusicVolume(float volume)
        {
            _audioSettings.SetMusicVolume(volume);
            TypedEvents.MusicVolumeChanged.Dispatch(volume);
        }
        
        public void SetSFXVolume(float volume)
        {
            _audioSettings.SetSFXVolume(volume);
            TypedEvents.SfxVolumeChanged.Dispatch(volume);
        }
        
        public void SetVoiceVolume(float volume)
        {
            _audioSettings.SetVoiceVolume(volume);
            TypedEvents.VoiceVolumeChanged.Dispatch(volume);
        }

        #endregion

        #region Private Methods

        private void StopCurrentVoice()
        {
            // Stop the current voice coroutine if it's running
            if (_currentVoiceCoroutine != null)
            {
                StopCoroutine(_currentVoiceCoroutine);
                _currentVoiceCoroutine = null;
            }

            // Stop any voice audio that might be playing
            if (_voiceSource.isPlaying)
            {
                _voiceSource.Stop();
            }

            // Reset ducking state if no voice is playing
            if (_isDucking)
            {
                _isDucking = false;
                // Restore the exact pre-duck AudioSource volumes (NOT the settings
                // module values — those are mixer-domain and would stick a permanent
                // double attenuation onto the sources).
                if (_musicSource != null) _musicSource.volume = _preDuckMusicVolume;
                if (_sfxSource != null) _sfxSource.volume = _preDuckSfxVolume;
            }

            // Release the current voice clip
            if (_currentVoiceKey != null)
            {
                ReleaseAudioClip(_currentVoiceKey.Value);
                _currentVoiceKey = null;
            }
            _currentVoiceClip = null;
        }

        #region Coroutines

        private System.Collections.IEnumerator PlayMusicCoroutine(AudioClip clip)
        {
            if (_musicSource.isPlaying)
            {
                yield return StartCoroutine(StopMusicCoroutine(_musicFadeDuration));
            }

            // The previous track is no longer in use — safe to release now.
            ReleasePendingMusicTrack();

            _musicSource.clip = clip;
            _musicSource.loop = true;
            _musicSource.volume = 0f;
            _musicSource.Play();

            float elapsedTime = 0f;
            while (elapsedTime < _musicFadeDuration)
            {
                _musicSource.volume = Mathf.Lerp(0f, 1f, elapsedTime / _musicFadeDuration);
                elapsedTime += Time.deltaTime;
                yield return null;
            }

            _musicSource.volume = 1f;
            _currentMusicCoroutine = null;
        }

        private System.Collections.IEnumerator StopMusicCoroutine(float fadeOutDuration)
        {
            float startVolume = _musicSource.volume;
            float elapsedTime = 0f;
            while (elapsedTime < fadeOutDuration)
            {
                _musicSource.volume = Mathf.Lerp(startVolume, 0f, elapsedTime / fadeOutDuration);
                elapsedTime += Time.deltaTime;
                yield return null;
            }
            _musicSource.Stop();
            _musicSource.volume = startVolume;
        }

        private System.Collections.IEnumerator StopMusicAndReleaseCoroutine(
            float fadeOutDuration,
            (string collection, string id, AudioLibrary.AudioType type) key)
        {
            yield return StartCoroutine(StopMusicCoroutine(fadeOutDuration));
            ReleaseAudioClip(key);
        }

        private System.Collections.IEnumerator PlayVoiceWithDuckingCoroutine(AudioClip clip, float volume, AudioSource audioSource = null)
        {
            if (!_isDucking)
            {
                // Capture the true pre-duck source volumes in fields so every
                // restore path (normal finish AND interrupt via StopCurrentVoice)
                // returns to exactly these values. Capturing locals while already
                // ducked would bake the ducked level in as the new "original".
                _preDuckMusicVolume = _musicSource.volume;
                _preDuckSfxVolume = _sfxSource.volume;

                _isDucking = true;
                float elapsedTime = 0f;
                while (elapsedTime < _duckingFadeTime)
                {
                    float t = elapsedTime / _duckingFadeTime;
                    _musicSource.volume = Mathf.Lerp(_preDuckMusicVolume, _preDuckMusicVolume * _duckingAmount, t);
                    _sfxSource.volume = Mathf.Lerp(_preDuckSfxVolume, _preDuckSfxVolume * _duckingAmount, t);
                    elapsedTime += Time.deltaTime;
                    yield return null;
                }
            }

            if (audioSource == null)
                _voiceSource.PlayOneShot(clip, volume);
            else
            {
                audioSource.outputAudioMixerGroup = _audioSettings.audioMixerVO;
                audioSource.PlayOneShot(clip, volume);
            }

            yield return new WaitForSeconds(clip.length);

            float restoreElapsedTime = 0f;
            while (restoreElapsedTime < _duckingFadeTime)
            {
                float t = restoreElapsedTime / _duckingFadeTime;
                _musicSource.volume = Mathf.Lerp(_preDuckMusicVolume * _duckingAmount, _preDuckMusicVolume, t);
                _sfxSource.volume = Mathf.Lerp(_preDuckSfxVolume * _duckingAmount, _preDuckSfxVolume, t);
                restoreElapsedTime += Time.deltaTime;
                yield return null;
            }

            // Land exactly on the pre-duck values (the lerp loop can exit below 1.0).
            _musicSource.volume = _preDuckMusicVolume;
            _sfxSource.volume = _preDuckSfxVolume;

            _isDucking = false;

            // Playback finished normally — release and clear voice tracking.
            if (_currentVoiceKey != null)
            {
                ReleaseAudioClip(_currentVoiceKey.Value);
                _currentVoiceKey = null;
            }
            _currentVoiceClip = null;
            _currentVoiceCoroutine = null;
        }

        private System.Collections.IEnumerator ReleaseAfterDelay(
            (string collection, string id, AudioLibrary.AudioType type) key,
            float delay)
        {
            // Realtime, not scaled: AudioSource playback ignores Time.timeScale, so a
            // paused game (timeScale 0) must not keep the clip's ref alive forever —
            // and a fast-forwarded one must not release it mid-playback.
            yield return new WaitForSecondsRealtime(delay);
            ReleaseAudioClip(key);
        }

        #endregion

        private void OnDestroy()
        {
            if (_instance == this)
            {
                // Force-release every loaded clip regardless of refcount — the
                // underlying Addressables handle was acquired once per key.
                foreach (var key in _loadedClips.Keys.ToArray())
                {
                    ReleaseLibraryAsset(key);
                }
                _loadedClips.Clear();
                _currentMusicKey = null;
                _currentVoiceKey = null;

                // Clear dialog play time tracking
                DialogAudioReference.ClearAllPlayTimeTracking();

                _instance = null;
            }
        }
        #endregion
    }
} 