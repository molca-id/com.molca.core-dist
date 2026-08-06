using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Molca.Editor.Addons;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Molca.Editor.Licensing
{
    /// <summary>
    /// Reports how a player build attempt ended to the Molca control plane, against the build token minted
    /// for it, so a project has a server-side answer to "what did we ship, and what is failing".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/Licensing/</c> — it authenticates with the
    /// developer entitlement and joins on the build token, both of which live here.
    /// </para>
    /// <para>
    /// <b>Durable, like usage reporting.</b> A report is written to <c>Library/Molca/BuildRecords</c>
    /// before anything is sent, so an offline machine, a domain reload, or an editor crash delays delivery
    /// rather than losing it. A build takes minutes and is often the last thing someone does before closing
    /// the editor; a fire-and-forget POST would lose exactly the builds that matter most.
    /// </para>
    /// <para>
    /// <b>What is sent</b> is the provenance of the attempt: profile, target, outcome, reason code,
    /// semantic version, build number, commit, branch, Unity version, size, duration and scene count.
    /// <b>What is not sent</b> is the output path — it names a person and their directory layout, and
    /// nothing server-side can act on it. Identity is not sent either: the server takes it from the signed
    /// entitlement and project binding on the request, so a client cannot attribute a build to anyone else.
    /// </para>
    /// <para>
    /// <b>A failure is reported as a code, never as a message.</b> Since <c>appBuildRecord</c> 2 this
    /// reports refused and failed attempts too, because "what is failing" is the question a project's
    /// health actually turns on. What it sends is a <see cref="Molca.Editor.MolcaBuildReasonCode"/> — a
    /// gate id, a step id, <c>build-failed</c> — and never <c>record.detail</c>, which is written for the
    /// person at this machine and may name a scene, a path, or a count. The server enforces the same split
    /// with a pattern rather than a length limit, so console output cannot be stored there even by a
    /// client that tries.
    /// </para>
    /// <para>
    /// <b>No opt-out switch, deliberately.</b> This reports only builds that already minted a
    /// project-scoped build token — that is, builds the control plane authorized and already has a row
    /// for. A ledger that individual machines could silently switch off would be worse than none, because
    /// a gap in it would be indistinguishable from a build that never happened. A project that does not
    /// want the ledger disconnects the project, and then no build token is minted and nothing is reported.
    /// </para>
    /// <para>
    /// <b>The gap that remains.</b> An attempt that ends before the license gate mints a token cannot be
    /// reported at all — an invalid profile, an unresolvable scene set, the pre-build Doctor gate, or the
    /// license gate itself refusing. Those are recorded locally in <c>Library/Molca/build-history.json</c>
    /// and nowhere else, and the health view says so rather than reporting them as builds that never
    /// happened.
    /// </para>
    /// </remarks>
    [InitializeOnLoad]
    internal static class ControlPlaneBuildRecorder
    {
        /// <summary>Build-context key carrying the build-token id minted for the running build.</summary>
        /// <remarks>Set by <see cref="LicenseBuildGate"/>; absent for any build that minted no token.</remarks>
        internal const string BuildIdKey = "license.buildTokenId";

        private const int MaxQueuedFiles = 200;
        private const int RequestTimeoutSeconds = 30;

        private static bool _flushing;

        /// <summary>
        /// Retries undelivered reports on every domain load.
        /// </summary>
        /// <remarks>
        /// Without this, a report that could not be delivered — the machine was offline, the entitlement had
        /// expired — would wait for the <em>next build</em> to be queued rather than the next time the editor
        /// loads, because queueing is the only other thing that flushes. Builds can be weeks apart, which
        /// would have made "delivery is delayed, not lost" true only in the letter. Costs a directory
        /// existence check when the spool is empty, which is almost always.
        /// </remarks>
        static ControlPlaneBuildRecorder()
        {
            EditorApplication.delayCall += Flush;
        }

        /// <summary>Queues one completed build for delivery. Never throws.</summary>
        /// <param name="buildId">The build-token id this build was authorized under.</param>
        /// <param name="record">The local build record produced by <see cref="BuildManager"/>.</param>
        /// <param name="sceneCount">How many scenes the player shipped.</param>
        /// <returns>True when the report was queued; false when it could not be.</returns>
        internal static bool Queue(string buildId, MolcaBuildRecord record, int sceneCount)
        {
            if (string.IsNullOrWhiteSpace(buildId) || record == null)
                return false;

            try
            {
                Directory.CreateDirectory(QueueDirectory);

                // A machine that has been offline for a long time must not spool without bound. Builds are
                // rare enough that 200 is months of history; the oldest is dropped rather than the newest,
                // because the recent ones are the ones anybody is looking for.
                var queued = Directory.GetFiles(QueueDirectory, "*.json")
                    .OrderBy(path => path, StringComparer.Ordinal).ToArray();
                for (int i = 0; i <= queued.Length - MaxQueuedFiles; i++)
                    TryDelete(queued[i]);

                var payload = BuildPayload(buildId, record, sceneCount);
                File.WriteAllText(
                    Path.Combine(QueueDirectory, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{buildId}.json"),
                    payload.ToString(Formatting.None));

                EditorApplication.delayCall += Flush;
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Molca] Could not queue the build record for the control plane: {exception.Message}");
                return false;
            }
        }

        /// <summary>
        /// Builds the request body for one completed build.
        /// </summary>
        /// <param name="buildId">The build-token id.</param>
        /// <param name="record">The local build record.</param>
        /// <param name="sceneCount">How many scenes the player shipped.</param>
        /// <returns>The payload, including the build id so the queue file is self-contained.</returns>
        /// <remarks>
        /// Internal so a test can assert what leaves the machine — in particular that the output path does
        /// not, which is the kind of thing a later refactor adds back by accident while "including
        /// everything useful".
        /// </remarks>
        internal static JObject BuildPayload(string buildId, MolcaBuildRecord record, int sceneCount) =>
            new JObject
            {
                ["buildId"] = buildId,
                ["profile"] = record.profile ?? string.Empty,
                ["buildTarget"] = record.target ?? string.Empty,
                // `appBuildRecord` 2. The outcome is what makes "what is failing" answerable server-side,
                // and the reason code is the *only* part of a failure that leaves the machine — never
                // `record.detail`, which is a sentence for the person here and may name a scene or a path.
                ["outcome"] = record.Outcome.ToString().ToLowerInvariant(),
                ["reasonCode"] = record.reasonCode ?? string.Empty,
                ["semanticVersion"] = record.semanticVersion ?? string.Empty,
                ["buildNumber"] = record.buildNumber ?? string.Empty,
                ["commit"] = record.commit ?? string.Empty,
                ["branch"] = record.branch ?? string.Empty,
                ["unityVersion"] = Application.unityVersion,
                ["totalSizeBytes"] = record.totalSizeBytes,
                ["durationSeconds"] = record.durationSeconds,
                ["sceneCount"] = sceneCount,
                ["builtAt"] = string.IsNullOrEmpty(record.timestampUtc)
                    ? DateTime.UtcNow.ToString("O")
                    : record.timestampUtc,
            };

        /// <summary>Sends queued reports oldest-first, deleting only what the server accounted for.</summary>
        private static async void Flush()
        {
            if (_flushing || !Directory.Exists(QueueDirectory)) return;
            _flushing = true;
            try
            {
                foreach (string file in Directory.GetFiles(QueueDirectory, "*.json")
                             .OrderBy(path => path, StringComparer.Ordinal))
                {
                    JObject payload;
                    try
                    {
                        payload = JObject.Parse(File.ReadAllText(file));
                    }
                    catch
                    {
                        TryDelete(file); // Corrupt spool entry: drop it rather than block the queue.
                        continue;
                    }

                    var outcome = await SendAsync(payload, CancellationToken.None);
                    if (outcome == SendOutcome.Retry)
                        return; // Offline, unlicensed, or unbound: everything after this waits too.

                    TryDelete(file);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Molca] Build-record delivery will retry after the next editor reload: {exception.Message}");
            }
            finally { _flushing = false; }
        }

        private enum SendOutcome
        {
            /// <summary>The server recorded it, already had it, or will never accept it.</summary>
            Done,

            /// <summary>Nothing was decided; keep the report and try later.</summary>
            Retry,
        }

        /// <summary>POSTs one report.</summary>
        private static async Awaitable<SendOutcome> SendAsync(JObject payload, CancellationToken cancellationToken)
        {
            if (!DevLicenseConfig.IsConfigured)
                return SendOutcome.Retry; // No control plane configured for this distribution yet.

            string entitlement = DevEntitlementStore.LoadEffective();
            if (DevEntitlementVerifier.Evaluate(entitlement, SystemInfo.deviceUniqueIdentifier, out _) != DevLicenseStatus.Valid)
                return SendOutcome.Retry;

            string projectBinding = MolcaProjectSettings.Instance?.ProjectBinding;
            if (string.IsNullOrWhiteSpace(projectBinding))
                return SendOutcome.Retry;

            string buildId = payload.Value<string>("buildId");
            if (string.IsNullOrWhiteSpace(buildId))
                return SendOutcome.Done; // Nothing to record it against; keeping it would wedge the queue.

            string url = DevLicenseConfig.ServerBaseUrl.TrimEnd('/')
                + $"/builds/{Uri.EscapeDataString(buildId)}/record";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !AddonDistributionConfig.IsTrustedDownloadHost(uri.Host))
                return SendOutcome.Retry;

            var body = new JObject(payload) { ["projectBinding"] = projectBinding };
            body.Remove("buildId"); // It is the path; sending it twice invites the two to disagree.

            using var request = new UnityWebRequest(uri.AbsoluteUri, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body.ToString(Formatting.None))),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = RequestTimeoutSeconds,
            };
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + entitlement);
            request.SetRequestHeader("X-Molca-Machine-Id", SystemInfo.deviceUniqueIdentifier);

            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Awaitable.NextFrameAsync(cancellationToken);
            }

            switch (request.responseCode)
            {
                case 200: // Already recorded — the build is in the ledger, which is all this wanted.
                case 201:
                    return SendOutcome.Done;

                // A payload the server will never accept, or a build id it does not have and never will.
                // Retrying forever would wedge every later report behind one permanently bad one.
                case 400:
                case 404:
                    Debug.LogWarning(
                        $"[Molca] The control plane rejected a build record ({request.responseCode}); " +
                        $"it will not be retried. {Describe(request)}");
                    return SendOutcome.Done;

                case 403:
                    Debug.LogWarning(
                        "[Molca] The control plane refused a build record for this project. An owner or " +
                        "manager should verify the project connection in Molca Hub > Settings > Project.");
                    return SendOutcome.Retry;

                default:
                    return request.result == UnityWebRequest.Result.Success
                        ? SendOutcome.Done
                        : SendOutcome.Retry;
            }
        }

        private static string Describe(UnityWebRequest request)
        {
            var text = request.downloadHandler?.text;
            return string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
        }

        private static void TryDelete(string file)
        {
            try { File.Delete(file); } catch { /* Retried on the next flush. */ }
        }

        private static string QueueDirectory =>
            Path.Combine(Directory.GetParent(Application.dataPath)?.FullName ?? ".",
                "Library", "Molca", "BuildRecords");

        /// <summary>How many reports are waiting to be delivered. For diagnostics and tests.</summary>
        internal static int QueuedCount =>
            Directory.Exists(QueueDirectory) ? Directory.GetFiles(QueueDirectory, "*.json").Length : 0;
    }

    /// <summary>
    /// Reports every successful Molca build to the control plane.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/Licensing/</c>.
    /// <b>Registration:</b> implements <see cref="IMolcaPostBuildStep"/>; discovered by
    /// <see cref="MolcaBuildStepRegistry"/>.
    /// </para>
    /// <para>
    /// This is what the post-build step contract was introduced for: work that only makes sense once an
    /// artifact exists, that must not be able to fail the build, and that needs the profile and the build
    /// facts a raw Unity postprocessor is never given.
    /// </para>
    /// <para>
    /// Skips silently — not a failure — for a build with no minted build token: <c>File &gt; Build</c>, a
    /// project that is not connected, or a distribution where licensing is not configured. There is nothing
    /// on the control plane for such a build to be a record of.
    /// </para>
    /// </remarks>
    public sealed class ControlPlaneBuildRecordStep : IMolcaPostBuildStep
    {
        /// <inheritdoc/>
        public string Id => "control-plane-build-record";

        /// <inheritdoc/>
        public string DisplayName => "Record the build with the control plane";

        /// <summary>Runs late: this reports what happened and changes nothing another step reads.</summary>
        public int Order => 900;

        /// <inheritdoc/>
        public bool ShouldRun(MolcaPostBuildContext context) =>
            context != null && !string.IsNullOrEmpty(BuildIdOf(context));

        /// <inheritdoc/>
        public MolcaBuildStepResult Run(MolcaPostBuildContext context)
        {
            string buildId = BuildIdOf(context);
            int sceneCount = context.Profile != null &&
                context.Profile.TryResolveScenePaths(out var scenes, out _) && scenes != null
                    ? scenes.Length
                    : EditorBuildSettings.scenes.Count(scene => scene.enabled);

            return ControlPlaneBuildRecorder.Queue(buildId, context.Record, sceneCount)
                ? MolcaBuildStepResult.Ok($"queued for the control plane (build {buildId}).")
                : MolcaBuildStepResult.Fail(
                    "the build record could not be queued for the control plane; this build will be " +
                    "missing from the project's build history.");
        }

        private static string BuildIdOf(MolcaPostBuildContext context) =>
            context.BuildContext?.GetValue(ControlPlaneBuildRecorder.BuildIdKey);
    }
}
