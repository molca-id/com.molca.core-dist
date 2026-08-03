using UnityEngine;
using Molca;
using System;
using Molca.Networking.Http;
using Molca.Modals;
using Molca.Localization;

namespace Molca.App
{
    /// <summary>
    /// SDK app-level subsystem that surfaces backend connection failures as a localized confirmation modal.
    /// Resolved like any <see cref="RuntimeSubsystem"/> via <c>RuntimeManager.GetSubsystem&lt;GameManager&gt;()</c>;
    /// it holds no static state.
    /// </summary>
    /// <remarks>
    /// Subscribes to <see cref="IHttpClient.ConnectionError"/> once the runtime is initialized and, when
    /// enabled, shows <see cref="connectionFailConfirmation"/>. Wired through <c>SDK Subsystems.prefab</c>.
    /// </remarks>
    public class GameManager : RuntimeSubsystem
    {
        [Header("Connection Settings")]
        [SerializeField] private bool enableConnectionFailConfirmation;
        [SerializeField] private ModalConfirmationHelper connectionFailConfirmation;

        private bool _isInitialized;

        /// <inheritdoc/>
        public override void Initialize(Action<IRuntimeSubsystem> finishCallback)
        {
            if (_isInitialized)
            {
                Debug.LogWarning("GameManager is already initialized!");
                finishCallback?.Invoke(this);
                return;
            }

            _isInitialized = true;

            SetupEventListeners();

            finishCallback?.Invoke(this);
        }

        private IHttpClient _http;

        // async void shim: subscription must wait for HttpClient's service to exist
        // (the legacy static event tolerated any init order; the instance event
        // needs the live IHttpClient). Body is fully try/caught per async contract.
        private async void SetupEventListeners()
        {
            try
            {
                await RuntimeManager.WaitForInitialization();
                _http = RuntimeManager.GetService<IHttpClient>();
                if (_http != null)
                    _http.ConnectionError += OnConnectionError;
            }
            catch (Exception e)
            {
                Debug.LogError($"GameManager: failed to subscribe to HTTP connection errors: {e.Message}");
            }
        }

        private async void OnConnectionError(string error)
        {
            if (!enableConnectionFailConfirmation || string.IsNullOrEmpty(error))
                return;

            try
            {
                await Awaitable.WaitForSecondsAsync(.2f);
                if (connectionFailConfirmation != null)
                {
                    connectionFailConfirmation.confirmationData.message.SetTextForLanguage(LocalizationManager.DefaultLanguageCode, error);
                    connectionFailConfirmation.Create();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error showing connection failure modal: {e.Message}");
            }
        }

        private void OnDestroy()
        {
            if (_http != null)
                _http.ConnectionError -= OnConnectionError;
        }
    }
}