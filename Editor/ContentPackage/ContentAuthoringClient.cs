using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Molca.ContentPackage.Editor
{
    /// <summary>
    /// Publishes a release candidate through the Molca control plane.
    ///
    /// The author never sees or holds storage credentials. The server issues short-lived presigned
    /// URLs for exactly the objects it expects, at keys it assigned, and the editor PUTs bytes to
    /// them — so publishing needs no cloud CLI, no bucket configuration, and no provider account.
    /// That property is the point of the whole design, and it is why this client only ever talks to
    /// Molca and to URLs Molca handed it.
    ///
    /// Editor-only: it carries a developer session and must never reach a player build.
    /// </summary>
    public sealed class ContentAuthoringClient
    {
        /// <summary>Progress for a publish in flight.</summary>
        public readonly struct PublishProgress
        {
            /// <summary>What the client is doing, for display.</summary>
            public string Stage { get; }

            /// <summary>Objects uploaded so far.</summary>
            public int Completed { get; }

            /// <summary>Objects that need uploading.</summary>
            public int Total { get; }

            /// <summary>Bytes uploaded so far.</summary>
            public long BytesSent { get; }

            /// <summary>Bytes that need uploading.</summary>
            public long BytesTotal { get; }

            /// <summary>Fraction complete, 0..1.</summary>
            public float Fraction => BytesTotal <= 0 ? 0f : Mathf.Clamp01((float)((double)BytesSent / BytesTotal));

            internal PublishProgress(string stage, int completed, int total, long bytesSent, long bytesTotal)
            {
                Stage = stage; Completed = completed; Total = total;
                BytesSent = bytesSent; BytesTotal = bytesTotal;
            }
        }

        /// <summary>The outcome of a publish.</summary>
        public sealed class PublishResult
        {
            /// <summary>True when the release reached the requested state.</summary>
            public bool Success;

            /// <summary>Machine-readable reason on failure.</summary>
            public string Reason = string.Empty;

            /// <summary>Human-readable detail.</summary>
            public string Message = string.Empty;

            /// <summary>The release, once a draft exists.</summary>
            public string ReleaseId = string.Empty;

            /// <summary>The signed manifest digest, once finalized.</summary>
            public string ManifestSha256 = string.Empty;

            /// <summary>True when the release was promoted to its channel.</summary>
            public bool Promoted;

            /// <summary>True when the user cancelled.</summary>
            public bool Cancelled;
        }

        private const int UploadConcurrency = 4;
        private const int UploadAttempts = 3;

        private readonly string _baseUrl;
        private readonly string _projectId;
        private readonly Func<string> _authorizationProvider;

        /// <param name="baseUrl">Control-plane origin, e.g. <c>https://unity.molca.id</c>.</param>
        /// <param name="projectId">The bound project's UUID. Authority is the UUID, never the name.</param>
        /// <param name="authorizationProvider">
        /// Supplies the current developer credential per request. A provider rather than a stored
        /// string so the credential is never held longer than a call, and never serialized.
        /// </param>
        public ContentAuthoringClient(string baseUrl, string projectId, Func<string> authorizationProvider)
        {
            _baseUrl = (baseUrl ?? string.Empty).TrimEnd('/');
            _projectId = projectId;
            _authorizationProvider = authorizationProvider;
        }

        /// <summary>
        /// Rejects a destination that is not a secure origin.
        ///
        /// This client is a sanctioned direct-transport exception, so the policy the routed pipeline
        /// would have applied is enforced here instead. A developer entitlement travels to the
        /// control plane and presigned URLs come back; neither may cross a plaintext connection.
        /// Loopback is allowed so a local control plane can be developed against.
        /// </summary>
        private static bool IsSecureOrigin(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme == Uri.UriSchemeHttps) return true;
            return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
        }

        private string Api => $"{_baseUrl}/customer/api/projects/{_projectId}/content";

        /// <summary>
        /// Creates a draft, uploads every missing object, finalizes, and optionally promotes.
        ///
        /// Safe to re-run after an interruption: the draft is keyed by the candidate's content, and
        /// objects already in storage are skipped rather than re-sent.
        /// </summary>
        /// <param name="candidate">The candidate to publish.</param>
        /// <param name="promote">Whether to make the release active once verified.</param>
        /// <param name="progress">Optional progress sink.</param>
        /// <param name="cancellationToken">Cancels between steps and between uploads.</param>
        public async Task<PublishResult> PublishAsync(
            ContentReleaseCandidate candidate,
            bool promote,
            IProgress<PublishProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            var result = new PublishResult();
            if (candidate == null) { result.Reason = "candidate_missing"; return result; }

            if (!IsSecureOrigin(_baseUrl))
            {
                result.Reason = "insecure_control_plane";
                result.Message =
                    $"Refusing to publish to '{_baseUrl}': the control plane must be HTTPS (or loopback). " +
                    "A developer credential and presigned upload URLs travel over this connection.";
                return result;
            }

            try
            {
                progress?.Report(new PublishProgress("Creating draft", 0, 0, 0, 0));
                var draft = await PostAsync($"{Api}/releases", new DraftRequest
                {
                    candidate = candidate,
                    idempotencyKey = candidate.IdempotencyKey,
                }, cancellationToken);

                if (!draft.ok) { result.Reason = draft.reason; result.Message = draft.body; return result; }

                var draftResponse = JsonUtility.FromJson<DraftResponse>(draft.body);
                result.ReleaseId = draftResponse?.release?.id;
                if (string.IsNullOrEmpty(result.ReleaseId))
                {
                    result.Reason = "draft_malformed";
                    result.Message = "The server accepted the draft but returned no release id.";
                    return result;
                }

                var byId = candidate.objects.ToDictionary(entry => entry.objectId, entry => entry, StringComparer.Ordinal);
                var uploads = draftResponse.uploads;

                for (int round = 1; uploads != null && uploads.pending > 0; round++)
                {
                    if (round > 3)
                    {
                        result.Reason = "upload_incomplete";
                        result.Message = "Objects were still pending after three upload rounds.";
                        return result;
                    }

                    var pending = uploads.objects ?? Array.Empty<UploadInstruction>();
                    long totalBytes = pending.Sum(instruction => instruction.sizeBytes);
                    var uploaded = await UploadAllAsync(pending, byId, totalBytes, progress, cancellationToken);
                    if (!uploaded.ok) { result.Reason = uploaded.reason; result.Message = uploaded.message; return result; }

                    var refreshed = await PostAsync($"{Api}/releases/{result.ReleaseId}/uploads", null, cancellationToken);
                    if (!refreshed.ok) { result.Reason = refreshed.reason; result.Message = refreshed.body; return result; }
                    uploads = JsonUtility.FromJson<UploadsResponse>(refreshed.body);
                }

                progress?.Report(new PublishProgress("Verifying", 0, 0, 0, 0));
                var finalized = await PostAsync($"{Api}/releases/{result.ReleaseId}/finalize", null, cancellationToken);
                if (!finalized.ok) { result.Reason = finalized.reason; result.Message = finalized.body; return result; }

                var finalizeResponse = JsonUtility.FromJson<FinalizeResponse>(finalized.body);
                result.ManifestSha256 = finalizeResponse?.release?.manifestSha256 ?? string.Empty;

                if (!promote)
                {
                    result.Success = true;
                    return result;
                }

                progress?.Report(new PublishProgress("Promoting", 0, 0, 0, 0));
                var promoted = await PostAsync($"{Api}/releases/{result.ReleaseId}/promote", null, cancellationToken);
                if (!promoted.ok) { result.Reason = promoted.reason; result.Message = promoted.body; return result; }

                result.Promoted = true;
                result.Success = true;
                return result;
            }
            catch (OperationCanceledException)
            {
                // A cancelled publish leaves a draft, not a broken release: nothing is active until
                // promotion, and re-running resumes the same draft.
                result.Cancelled = true;
                result.Reason = "cancelled";
                return result;
            }
            catch (Exception ex)
            {
                result.Reason = "unexpected_error";
                result.Message = ex.Message;
                return result;
            }
        }

        private async Task<(bool ok, string reason, string message)> UploadAllAsync(
            IReadOnlyList<UploadInstruction> instructions,
            IReadOnlyDictionary<string, ContentReleaseCandidate.ObjectEntry> byId,
            long totalBytes,
            IProgress<PublishProgress> progress,
            CancellationToken cancellationToken)
        {
            var queue = new Queue<UploadInstruction>(instructions);
            int completed = 0;
            long sent = 0;
            var failure = (reason: string.Empty, message: string.Empty);

            async Task Worker()
            {
                while (true)
                {
                    UploadInstruction instruction;
                    lock (queue)
                    {
                        if (queue.Count == 0 || !string.IsNullOrEmpty(failure.reason)) return;
                        instruction = queue.Dequeue();
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    if (!byId.TryGetValue(instruction.objectId, out var entry) || !File.Exists(entry.localPath))
                    {
                        lock (queue)
                        {
                            failure = ("object_missing_locally",
                                $"The server expects object '{instruction.objectId}', which is not in the staged build.");
                        }
                        return;
                    }

                    string error = null;
                    for (int attempt = 1; attempt <= UploadAttempts; attempt++)
                    {
                        error = await PutObjectAsync(instruction, entry.localPath, cancellationToken);
                        if (error == null) break;
                        if (attempt < UploadAttempts)
                            await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken);
                    }

                    if (error != null)
                    {
                        lock (queue) { failure = ("upload_failed", error); }
                        return;
                    }

                    lock (queue)
                    {
                        completed++;
                        sent += instruction.sizeBytes;
                        progress?.Report(new PublishProgress(
                            $"Uploading {completed}/{instructions.Count}", completed, instructions.Count, sent, totalBytes));
                    }
                }
            }

            var workers = Enumerable.Range(0, Math.Min(UploadConcurrency, Math.Max(1, instructions.Count)))
                .Select(_ => Worker()).ToArray();
            await Task.WhenAll(workers);

            return string.IsNullOrEmpty(failure.reason)
                ? (true, string.Empty, string.Empty)
                : (false, failure.reason, failure.message);
        }

        /// <summary>
        /// PUTs one object to its presigned URL.
        ///
        /// Single-PUT by choice, not by necessity: the Phase 0 storage probe confirmed the provider
        /// supports multipart, so this is a simplification to revisit when bundles grow past what a
        /// single request should carry. It is also why no Molca credential is attached — the URL
        /// carries its own signature and adding an Authorization header would leak a developer
        /// session to the storage host.
        /// </summary>
        private static async Task<string> PutObjectAsync(
            UploadInstruction instruction, string localPath, CancellationToken cancellationToken)
        {
            // The URL came from the server, but it is still a destination this process is about to
            // send bytes to, and it is checked like any other.
            if (!IsSecureOrigin(instruction.url))
                return $"Refusing to upload '{instruction.objectId}' over an insecure connection.";

            byte[] payload;
            try { payload = File.ReadAllBytes(localPath); }
            catch (Exception ex) { return $"Could not read '{Path.GetFileName(localPath)}': {ex.Message}"; }

            using var request = new UnityWebRequest(instruction.url, "PUT")
            {
                uploadHandler = new UploadHandlerRaw(payload),
                downloadHandler = new DownloadHandlerBuffer(),
            };

            foreach (var header in instruction.HeaderPairs())
                request.SetRequestHeader(header.Key, header.Value);

            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    request.Abort();
                    throw new OperationCanceledException(cancellationToken);
                }
                await Task.Yield();
            }

            if (request.result == UnityWebRequest.Result.Success) return null;

            // Never echo the URL: it is a signed credential for the duration of its life.
            return $"Upload of '{instruction.objectId}' failed with HTTP {request.responseCode}.";
        }

        private async Task<(bool ok, string reason, string body)> PostAsync(
            string url, object payload, CancellationToken cancellationToken)
        {
            string json = payload == null ? "{}" : JsonUtility.ToJson(payload);
            using var request = new UnityWebRequest(url, "POST")
            {
                uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(json)),
                downloadHandler = new DownloadHandlerBuffer(),
            };
            request.SetRequestHeader("Content-Type", "application/json");

            string authorization = _authorizationProvider?.Invoke();
            if (!string.IsNullOrEmpty(authorization)) request.SetRequestHeader("Authorization", authorization);

            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    request.Abort();
                    throw new OperationCanceledException(cancellationToken);
                }
                await Task.Yield();
            }

            string body = request.downloadHandler?.text ?? string.Empty;
            if (request.result == UnityWebRequest.Result.Success) return (true, string.Empty, body);

            string reason = ExtractReason(body);
            return (false, string.IsNullOrEmpty(reason) ? $"http_{request.responseCode}" : reason, body);
        }

        /// <summary>Pulls the server's machine-readable reason out of an error body, if present.</summary>
        private static string ExtractReason(string body)
        {
            if (string.IsNullOrEmpty(body)) return string.Empty;
            try { return JsonUtility.FromJson<ReasonEnvelope>(body)?.reason ?? string.Empty; }
            catch { return string.Empty; }
        }

        [Serializable] private sealed class ReasonEnvelope { public string reason; }

        [Serializable]
        private sealed class DraftRequest
        {
            public ContentReleaseCandidate candidate;
            public string idempotencyKey;
        }

        [Serializable] private sealed class ReleaseSummary { public string id; public string status; public string manifestSha256; }
        [Serializable] private sealed class DraftResponse { public ReleaseSummary release; public bool created; public UploadsResponse uploads; }
        [Serializable] private sealed class FinalizeResponse { public ReleaseSummary release; public bool alreadyVerified; }

        [Serializable]
        private sealed class UploadsResponse
        {
            public string releaseId;
            public int pending;
            public int present;
            public UploadInstruction[] objects;
        }

        [Serializable]
        internal sealed class UploadInstruction
        {
            public string objectId;
            public string sha256;
            public long sizeBytes;
            public string method;
            public string url;
            public string contentType;

            /// <summary>
            /// Headers to send with the PUT. JsonUtility cannot deserialize a dictionary, so the
            /// server's header map is not round-tripped; content type is the only one that matters
            /// for a presigned PUT, and the probe confirmed it is not part of the signature.
            /// </summary>
            public IEnumerable<KeyValuePair<string, string>> HeaderPairs()
            {
                if (!string.IsNullOrEmpty(contentType))
                    yield return new KeyValuePair<string, string>("Content-Type", contentType);
            }
        }
    }
}
