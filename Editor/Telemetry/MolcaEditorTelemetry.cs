using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Molca.Editor.Licensing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Molca.Editor.Telemetry
{
    /// <summary>
    /// Durable, batched reporter for framework usage from the Unity Editor. Events are appended to a
    /// queue under <c>Library/Molca/Telemetry</c> before anything is sent, so a domain reload, an editor
    /// crash, or an offline machine delays delivery instead of losing it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This supersedes the add-on-only install queue: add-on lifecycle events are now simply events named
    /// <c>addon.*</c> alongside <c>editor.*</c> and <c>build.*</c>. One queue, one endpoint, one schema.
    /// </para>
    /// <para>
    /// Reports carry no token, email, raw machine id, download URL, project name, or asset path — server
    /// attribution comes from the signed entitlement presented on the request, and the machine identifier
    /// is HMAC-hashed server-side. Governed by <see cref="MolcaEditorTelemetrySettings"/>.
    /// Not thread-safe; main thread only, like the rest of the editor API surface.
    /// </para>
    /// </remarks>
    [InitializeOnLoad]
    internal static class MolcaEditorTelemetry
    {
        private const int MaxBatch = 100;
        private const int MaxQueuedFiles = 2000;
        private const int RequestTimeoutSeconds = 30;

        /// <summary>Identifies one editor run, so multi-event flows can be stitched together server-side.</summary>
        private static readonly string SessionId = Guid.NewGuid().ToString("N");

        private static bool _flushing;
        private static bool _missingBindingWarned;

        static MolcaEditorTelemetry()
        {
            // The first load of a domain is the closest thing the editor has to a session start. Reloads
            // are frequent, so this is deliberately recorded once per editor process, not per domain.
            if (!SessionStateHasStarted())
            {
                SessionState.SetBool(SessionStartedKey, true);
                Track("editor.session.started", new Dictionary<string, object>
                {
                    { "unityVersion", Application.unityVersion },
                    { "platform", Application.platform.ToString() },
                });
            }
            EditorApplication.delayCall += Flush;
        }

        private const string SessionStartedKey = "Molca.Telemetry.SessionStarted";
        private static bool SessionStateHasStarted() => SessionState.GetBool(SessionStartedKey, false);

        /// <summary>True when the project has not opted out of usage reporting.</summary>
        internal static bool IsEnabled => MolcaEditorTelemetrySettings.FindExisting()?.Enabled ?? true;

        /// <summary>
        /// Queues one usage event for delivery. Never throws and never blocks the caller.
        /// </summary>
        /// <param name="name">
        /// Dotted <c>domain.event</c> name with two to four segments, e.g. <c>addon.install</c> or
        /// <c>editor.doctor.run</c>. Malformed names are rejected by the server, so keep to the dictionary
        /// in <c>Documentation~/reference/TELEMETRY.md</c>.
        /// </param>
        /// <param name="properties">Optional flat property bag. Must contain no personal or project data.</param>
        internal static void Track(string name, IReadOnlyDictionary<string, object> properties = null)
        {
            if (string.IsNullOrEmpty(name) || !IsEnabled) return;
            try
            {
                Directory.CreateDirectory(QueueDirectory);
                // A machine that has been offline for months must not grow an unbounded spool.
                if (Directory.GetFiles(QueueDirectory, "*.json").Length >= MaxQueuedFiles) return;

                var payload = new JObject
                {
                    ["name"] = name,
                    ["ts"] = DateTime.UtcNow.ToString("O"),
                    ["sessionId"] = SessionId,
                    ["coreVersion"] = AddonDistributionConfigVersion(),
                    ["editorVersion"] = Application.unityVersion,
                    ["platform"] = Application.platform.ToString(),
                };
                if (properties != null && properties.Count > 0)
                {
                    var bag = new JObject();
                    foreach (var pair in properties)
                        bag[pair.Key] = pair.Value == null ? JValue.CreateNull() : JToken.FromObject(pair.Value);
                    payload["properties"] = bag;
                }

                File.WriteAllText(Path.Combine(QueueDirectory, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json"),
                    payload.ToString(Formatting.None));
                if (MolcaEditorTelemetrySettings.FindExisting()?.Verbose == true)
                    Debug.Log($"[Molca Telemetry] queued {name}");
                EditorApplication.delayCall += Flush;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Molca Telemetry] Could not queue '{name}': {exception.Message}");
            }
        }

        /// <summary>Sends queued events oldest-first, deleting only what the server accepted.</summary>
        private static async void Flush()
        {
            if (_flushing || !IsEnabled || !Directory.Exists(QueueDirectory)) return;
            _flushing = true;
            try
            {
                string[] files = Directory.GetFiles(QueueDirectory, "*.json").OrderBy(path => path, StringComparer.Ordinal).ToArray();
                for (int offset = 0; offset < files.Length; offset += MaxBatch)
                {
                    var batch = files.Skip(offset).Take(MaxBatch).ToArray();
                    var events = new JArray();
                    var readable = new List<string>();
                    foreach (string file in batch)
                    {
                        try
                        {
                            events.Add(JObject.Parse(File.ReadAllText(file)));
                            readable.Add(file);
                        }
                        catch { TryDelete(file); } // Corrupt spool entry: drop it rather than block the queue.
                    }
                    if (readable.Count == 0) continue;
                    if (!await SendAsync(events, CancellationToken.None)) return; // Offline or unlicensed: retry later.
                    foreach (string file in readable) TryDelete(file);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Molca Telemetry] Delivery will retry after the next editor reload: {exception.Message}");
            }
            finally { _flushing = false; }
        }

        /// <summary>POSTs one batch. Returns false for any condition worth retrying later.</summary>
        private static async Awaitable<bool> SendAsync(JArray events, CancellationToken cancellationToken)
        {
            string token = DevEntitlementStore.LoadEffective();
            if (DevEntitlementVerifier.Evaluate(token, SystemInfo.deviceUniqueIdentifier, out _) != DevLicenseStatus.Valid)
                return false; // Unlicensed editors report nothing; the spool waits for a valid sign-in.

            string projectBinding = MolcaProjectSettings.Instance?.ProjectBinding;
            if (string.IsNullOrWhiteSpace(projectBinding))
            {
                if (!_missingBindingWarned)
                {
                    Debug.LogWarning("[Molca Telemetry] Events are queued but cannot be sent until an owner or " +
                                     "manager connects this repository in Molca Hub > Settings > Project.");
                    _missingBindingWarned = true;
                }
                return false;
            }
            _missingBindingWarned = false;

            string url = DevLicenseConfig.ServerBaseUrl.TrimEnd('/') + "/telemetry/events";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !AddonDistributionConfigTrusts(uri.Host)) return false;

            string body = BuildEnvelope(events, projectBinding).ToString(Formatting.None);
            using var request = new UnityWebRequest(uri.AbsoluteUri, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = RequestTimeoutSeconds,
            };
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + token);
            request.SetRequestHeader("X-Molca-Machine-Id", SystemInfo.deviceUniqueIdentifier);

            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Awaitable.NextFrameAsync(cancellationToken);
            }

            // A 400 means this client produced events the server will never accept; dropping them is
            // correct, since retrying forever would wedge the queue behind a permanently bad batch.
            if (request.responseCode == 400) return true;
            if (request.responseCode == 403 && !string.IsNullOrWhiteSpace(projectBinding))
                Debug.LogWarning("[Molca Telemetry] The current project connection was rejected. " +
                                 "An owner or manager should verify access in Molca Hub > Settings > Project.");
            return request.result == UnityWebRequest.Result.Success;
        }

        internal static JObject BuildEnvelope(JArray events, string projectBinding)
        {
            if (string.IsNullOrWhiteSpace(projectBinding))
                throw new InvalidOperationException("project_binding_required");
            var envelope = new JObject { ["events"] = events };
            envelope["projectBinding"] = projectBinding;
            return envelope;
        }

        private static void TryDelete(string file)
        {
            try { File.Delete(file); } catch { /* Retried on the next flush. */ }
        }

        private static string QueueDirectory =>
            Path.Combine(Directory.GetParent(Application.dataPath)?.FullName ?? ".", "Library", "Molca", "Telemetry");

        // Indirection kept deliberately narrow so this file does not depend on the Add-ons namespace's
        // internals beyond the two facts it needs: which Core is installed, and which hosts are pinned.
        private static string AddonDistributionConfigVersion() => Addons.AddonDistributionConfig.CoreVersion();
        private static bool AddonDistributionConfigTrusts(string host) => Addons.AddonDistributionConfig.IsTrustedDownloadHost(host);
    }
}
