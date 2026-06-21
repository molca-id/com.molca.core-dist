using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Flags usage of the legacy static-singleton surfaces (the [Obsolete] shims left
    /// by Sprint 5.1) and of runtime service lookup anti-patterns
    /// (<c>FindObjectOfType</c> for services, <c>DontDestroyOnLoad</c> outside
    /// RuntimeManager) in runtime code.
    /// </summary>
    public class StaticSingletonUsageCheck : IDoctorCheck
    {
        public string Id => "static-singleton-usage";
        public string Description => "Legacy static singleton APIs and FindObjectOfType/DontDestroyOnLoad anti-patterns";

        // Usage patterns (member access), never declarations. Internal plumbing
        // (HttpClient.Current, *Core methods) intentionally doesn't match.
        private static readonly (Regex pattern, string advice)[] Patterns =
        {
            (new Regex(@"\bHttpClient\.(SendAsync|Send|CreateRequest|AddDefaultHeader|RemoveDefaultHeader|ClearDefaultHeaders|CancelAllRequests|AddInterceptor|RemoveInterceptor|SetRetryPolicy|SetTransport|ClearHistory|BaseUrl|MaxConcurrentRequests|RequestHistory|ActiveRequestCount|OnRequestStarted|OnRequestCompleted|OnRequestFailed|OnConnectionError)\b"),
                "use IHttpClient via RuntimeManager.GetService<IHttpClient>() or [Inject]"),
            (new Regex(@"\bCacheManager\.(Cache|TryGetCache|GetCachePath|ClearCache|GetTempPath|CachePath|CacheSize|IsReady|IsCached)\b"),
                "use ICacheService via RuntimeManager.GetService<ICacheService>() or [Inject]"),
            (new Regex(@"\bSceneLoadManager\.(LoadScene|LoadAddressableScene|LoadNextScene|UnloadScene|UnloadAllAddressableScenes|TryGetNextSceneName|IsSceneLoaded|ActiveScene)\b"),
                "use ISceneLoader via RuntimeManager.GetService<ISceneLoader>() or [Inject]"),
            (new Regex(@"\bAudioManager\.Instance\b"),
                "use RuntimeManager.GetSubsystem<AudioManager>() or [Inject]"),
            (new Regex(@"\bColorModule\.(Instance|GetColor|HasColor|GetAllColorIds|GetColorIdsInSwatch|GetSwatchNames|GetColorWithFallback|ClearSavedColor|RefreshInstance)\b"),
                "use IColorSchemeService.ActiveScheme and the IColorProvider instance API"),
            (new Regex(@"\bColorSchemeManager\.(Instance|ActiveScheme|ActiveSchemeIndex|SchemeNames|SchemeCount|SetScheme|ToggleScheme|NextScheme|PreviousScheme|RefreshAllColorIDs|GetScheme|OnSchemeChanged)\b"),
                "use IColorSchemeService via RuntimeManager.GetService<IColorSchemeService>() or [Inject]"),
            (new Regex(@"\bFindObjectOfType\s*<|\bFindObjectsOfType\s*<"),
                "use RuntimeManager.GetSubsystem<T>() or [Inject] for services"),
            (new Regex(@"\bDontDestroyOnLoad\s*\("),
                "RuntimeManager owns persistence; never call DontDestroyOnLoad yourself"),
        };

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            // Pure text scan — run off the main thread so the editor stays responsive.
            await Awaitable.BackgroundThreadAsync();

            var issues = new List<DoctorIssue>();
            foreach (var source in context.RuntimeSources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // RuntimeManager legitimately owns DontDestroyOnLoad.
                bool isRuntimeManager = source.Path.EndsWith("/Runtime/Runtime/RuntimeManager.cs");

                for (int i = 0; i < source.Lines.Length; i++)
                {
                    var line = source.Lines[i];
                    if (DoctorContext.IsSuppressed(line))
                        continue;
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///"))
                        continue;

                    foreach (var (pattern, advice) in Patterns)
                    {
                        if (!pattern.IsMatch(line))
                            continue;
                        if (isRuntimeManager && line.Contains("DontDestroyOnLoad"))
                            continue;

                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Error,
                            $"Legacy static/singleton usage `{pattern.Match(line).Value}` — {advice}.",
                            source.Path, i + 1));
                    }
                }
            }
            return issues;
        }
    }
}
