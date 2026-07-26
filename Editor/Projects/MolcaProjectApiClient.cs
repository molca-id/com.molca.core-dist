using System;
using System.Text;
using System.Threading;
using Molca.Editor.Addons;
using Molca.Editor.Licensing;
using UnityEngine;
using UnityEngine.Networking;

namespace Molca.Editor.Projects
{
    /// <summary>Entitlement-authenticated client for visible projects and authorized binding issuance.</summary>
    internal sealed class MolcaProjectApiClient
    {
        private const int RequestTimeoutSeconds = 20;

        internal async Awaitable<ProjectApiResult<ProjectListResponse>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            var response = await SendAsync(UnityWebRequest.kHttpVerbGET, "/api/projects", null, cancellationToken);
            return Parse<ProjectListResponse>(response);
        }

        internal async Awaitable<ProjectApiResult<MolcaBackendProject>> CreateAsync(
            string name, CancellationToken cancellationToken = default)
        {
            string body = JsonUtility.ToJson(new CreateProjectRequest { name = name });
            var response = await SendAsync(UnityWebRequest.kHttpVerbPOST, "/api/projects", body, cancellationToken);
            return Parse<MolcaBackendProject>(response);
        }

        internal async Awaitable<ProjectApiResult<ProjectBindingResponse>> BindAsync(
            string projectId, CancellationToken cancellationToken = default)
        {
            string path = $"/api/projects/{Uri.EscapeDataString(projectId)}/bindings";
            var response = await SendAsync(UnityWebRequest.kHttpVerbPOST, path, "{}", cancellationToken);
            return Parse<ProjectBindingResponse>(response);
        }

        private static ProjectApiResult<T> Parse<T>(ProjectApiResult<string> response)
        {
            if (!response.Success) return ProjectApiResult<T>.Fail(response.Error);
            try
            {
                var value = JsonUtility.FromJson<T>(response.Value);
                return value == null
                    ? ProjectApiResult<T>.Fail("The project service returned an empty response.")
                    : ProjectApiResult<T>.Ok(value);
            }
            catch (Exception exception)
            {
                return ProjectApiResult<T>.Fail($"Could not parse the project response: {exception.Message}");
            }
        }

        private static async Awaitable<ProjectApiResult<string>> SendAsync(
            string method, string path, string body, CancellationToken cancellationToken)
        {
            string entitlement = DevEntitlementStore.LoadEffective();
            if (DevEntitlementVerifier.Evaluate(entitlement, SystemInfo.deviceUniqueIdentifier, out _) !=
                DevLicenseStatus.Valid)
                return ProjectApiResult<string>.Fail("Sign in with a valid Molca developer license first.");

            string url = DevLicenseConfig.ServerBaseUrl.TrimEnd('/') + path;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !AddonDistributionConfig.IsTrustedDownloadHost(uri.Host))
                return ProjectApiResult<string>.Fail("The project service URL is not trusted.");

            using var request = new UnityWebRequest(uri.AbsoluteUri, method)
            {
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = RequestTimeoutSeconds,
            };
            if (body != null)
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                request.SetRequestHeader("Content-Type", "application/json");
            }
            request.SetRequestHeader("Authorization", "Bearer " + entitlement);
            request.SetRequestHeader("X-Molca-Machine-Id", SystemInfo.deviceUniqueIdentifier);

            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Awaitable.NextFrameAsync(cancellationToken);
            }
            if (request.result == UnityWebRequest.Result.Success)
                return ProjectApiResult<string>.Ok(request.downloadHandler.text);

            string reason = null;
            try { reason = JsonUtility.FromJson<ProjectApiError>(request.downloadHandler.text)?.reason; }
            catch { /* Fall back to HTTP status below. */ }
            return ProjectApiResult<string>.Fail(reason switch
            {
                "capability_denied" => "Only a project owner or manager can perform this action.",
                "membership_required" => "Your current Molca membership is no longer active.",
                "project_not_found" => "The project is unavailable or you do not have access.",
                _ => $"Project service request failed (HTTP {request.responseCode}).",
            });
        }
    }
}
