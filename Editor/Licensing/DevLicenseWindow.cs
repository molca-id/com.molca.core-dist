using System;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Licensing
{
    /// <summary>
    /// Editor window for developer license activation: shows the current entitlement status and
    /// drives the interactive Google sign-in that produces one. Reached via
    /// <c>Molca &gt; License &gt; Developer Sign-In</c>.
    /// </summary>
    public sealed class DevLicenseWindow : EditorWindow
    {
        private string _status = string.Empty;
        private bool _busy;
        private CancellationTokenSource _cts;

        /// <summary>Opens the developer license window.</summary>
        [MenuItem("Molca/License/Developer Sign-In")]
        public static void Open()
        {
            var window = GetWindow<DevLicenseWindow>(true, "Molca Developer License");
            window.minSize = new Vector2(420, 220);
            window.Refresh();
            window.Show();
        }

        private void OnEnable() => Refresh();

        private void OnDisable()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        /// <summary>Re-evaluates the stored entitlement into a human-readable status line.</summary>
        private void Refresh()
        {
            if (!DevLicenseConfig.IsConfigured)
            {
                _status = "Licensing is not configured for this distribution (DevLicenseConfig placeholders).";
                return;
            }

            string token = DevEntitlementStore.LoadEffective();
            var status = DevEntitlementVerifier.Evaluate(token, SystemInfo.deviceUniqueIdentifier, out var payload);
            _status = status switch
            {
                DevLicenseStatus.Valid =>
                    $"Licensed: {payload.licenseeId} ({payload.email})\nValid until {payload.ExpiresAtUtc:u}.",
                DevLicenseStatus.Missing => "Not signed in on this machine.",
                DevLicenseStatus.Expired => "Entitlement expired — please sign in again.",
                DevLicenseStatus.WrongMachine => "Entitlement was issued for a different machine — sign in here.",
                DevLicenseStatus.Invalid => "Stored entitlement is invalid — please sign in again.",
                _ => "Unknown status.",
            };
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Developer License", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUILayout.HelpBox(_status, MessageType.Info);
            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(_busy || !DevLicenseConfig.IsConfigured))
            {
                if (GUILayout.Button(_busy ? "Signing in…" : "Sign in with Google", GUILayout.Height(30)))
                    BeginSignIn();
            }

            using (new EditorGUI.DisabledScope(_busy || string.IsNullOrEmpty(DevEntitlementStore.Load())))
            {
                if (GUILayout.Button("Sign out (clear entitlement)"))
                {
                    DevEntitlementStore.Clear();
                    Refresh();
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                "Builds are blocked until an authorized Google account is signed in.",
                EditorStyles.wordWrappedMiniLabel);
        }

        /// <summary>Unity UI entry point: runs activation and never lets exceptions escape.</summary>
        private async void BeginSignIn()
        {
            _busy = true;
            _status = "Opening browser for Google sign-in…";
            Repaint();

            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            try
            {
                var result = await DevLicenseClient.ActivateAsync(_cts.Token);
                if (result.Canceled)
                {
                    _status = "Sign-in canceled.";
                }
                else if (result.Success)
                {
                    _status = $"Licensed: {result.LicenseeId}\nValid until {result.ExpiresAt}.";
                    Debug.Log($"[License] Signed in: {result.LicenseeId} (valid until {result.ExpiresAt}).");
                }
                else
                {
                    // Surface the real reason to the console too — the window can be repainted/closed,
                    // and the console EditorHttpClient line only shows the HTTP status, not this reason.
                    _status = $"Sign-in failed: {result.Error}";
                    Debug.LogError($"[License] Sign-in failed: {result.Error}");
                }
            }
            catch (OperationCanceledException)
            {
                _status = "Sign-in canceled.";
            }
            catch (Exception e)
            {
                _status = $"Sign-in error: {e.Message}";
                Debug.LogError($"[License] Developer sign-in error: {e}");
            }
            finally
            {
                // NOTE: do not call Refresh() here — it would re-read the store and clobber the
                // success/failure message above with the generic stored-token status.
                _busy = false;
                Repaint();
            }
        }
    }
}
