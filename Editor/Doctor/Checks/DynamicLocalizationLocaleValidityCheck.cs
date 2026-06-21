using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Molca.Localization;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Validates the serialized <c>translations</c> of every
    /// <see cref="Molca.Localization.DynamicLocalization"/> in prefabs,
    /// ScriptableObjects, and open scenes against the language codes defined in the
    /// project's <see cref="LocalizationModule"/> assets. A translation row with a
    /// blank language code is unmatchable at runtime (it permanently pollutes the
    /// list); a row whose code is not a defined locale never resolves.
    /// </summary>
    /// <remarks>
    /// Codes from every <see cref="LocalizationModule"/> are unioned: a project may ship
    /// more than one module, so a code is treated as valid when <em>any</em> module
    /// defines it. Blank-code rows are always reported, even with no module present.
    ///
    /// Stays on the main thread (AssetDatabase/SerializedObject/SceneManager are
    /// main-thread only) and yields before each heavy asset so a large project stays
    /// responsive and cancellable. Closed scenes are not opened — run with the relevant
    /// scenes loaded.
    /// </remarks>
    public class DynamicLocalizationLocaleValidityCheck : IDoctorCheck
    {
        public string Id => "dynamic-localization-locale-invalid";
        public string Description => "DynamicLocalization translations with blank or unknown language codes";

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            await Awaitable.MainThreadAsync();
            var issues = new List<DoctorIssue>();

            var validCodes = new HashSet<string>();
            var modules = AssetDatabase.FindAssets("t:LocalizationModule")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<LocalizationModule>)
                .Where(m => m != null)
                .ToList();
            foreach (var module in modules)
                foreach (var code in module.LanguageCode)
                    if (!string.IsNullOrEmpty(code))
                        validCodes.Add(code);

            // When no module defines locales we can still flag blank-code rows, but not
            // unknown-code rows (there is nothing to compare against).
            bool canValidateCodes = validCodes.Count > 0;
            if (!canValidateCodes)
                issues.Add(new DoctorIssue(Id, DoctorSeverity.Info,
                    "No LocalizationModule defines language codes — only blank-code rows can be validated."));

            var assetPaths = AssetDatabase.FindAssets("t:Prefab t:ScriptableObject", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct()
                .Where(p => !context.IsIgnored(p))
                .ToList();

            for (int p = 0; p < assetPaths.Count; p++)
            {
                var path = assetPaths[p];
                context.ReportStatus($"Assets {p + 1}/{assetPaths.Count}");
                await EditorYieldAsync(cancellationToken);

                var main = AssetDatabase.LoadMainAssetAtPath(path);
                IEnumerable<Object> targets = main switch
                {
                    GameObject go => go.GetComponentsInChildren<MonoBehaviour>(true).Where(c => c != null).Cast<Object>(),
                    ScriptableObject so => new[] { (Object)so },
                    _ => Enumerable.Empty<Object>(),
                };
                issues.AddRange(ScanObjects(targets, path, validCodes, canValidateCodes));
            }

            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;
                if (!string.IsNullOrEmpty(scene.path) && context.IsIgnored(scene.path))
                    continue;

                context.ReportStatus($"Scene {scene.name}");
                await EditorYieldAsync(cancellationToken);
                var behaviours = scene.GetRootGameObjects()
                    .SelectMany(r => r.GetComponentsInChildren<MonoBehaviour>(true))
                    .Where(c => c != null)
                    .Cast<Object>();
                issues.AddRange(ScanObjects(behaviours, scene.path ?? scene.name, validCodes, canValidateCodes));
            }

            return issues;
        }

        private IEnumerable<DoctorIssue> ScanObjects(
            IEnumerable<Object> objects, string assetPath,
            HashSet<string> validCodes, bool canValidateCodes)
        {
            foreach (var obj in objects)
            {
                var serialized = new SerializedObject(obj);
                var property = serialized.GetIterator();
                bool enterChildren = true;
                while (property.Next(enterChildren))
                {
                    enterChildren = true;
                    if (property.propertyType != SerializedPropertyType.Generic)
                        continue;

                    // DynamicLocalization serializes as a generic struct/class with a
                    // `translations` array and the `useLocalizedString` flag (see the type).
                    var translations = property.FindPropertyRelative("translations");
                    var useLocalized = property.FindPropertyRelative("useLocalizedString");
                    if (translations == null || !translations.isArray || useLocalized == null)
                        continue;

                    enterChildren = false; // it's a DynamicLocalization; don't descend into it

                    // LocalizedString mode does not use the translations list.
                    if (useLocalized.boolValue)
                        continue;

                    for (int i = 0; i < translations.arraySize; i++)
                    {
                        var entry = translations.GetArrayElementAtIndex(i);
                        var code = entry.FindPropertyRelative("languageCode")?.stringValue;

                        if (string.IsNullOrEmpty(code))
                        {
                            yield return new DoctorIssue(Id, DoctorSeverity.Warning,
                                $"DynamicLocalization `{property.propertyPath}` on {obj.name} has a translation " +
                                $"(row {i}) with a blank language code — it is unmatchable at runtime.",
                                assetPath);
                            continue;
                        }

                        if (canValidateCodes && !validCodes.Contains(code))
                            yield return new DoctorIssue(Id, DoctorSeverity.Error,
                                $"DynamicLocalization `{property.propertyPath}` on {obj.name} has a translation " +
                                $"(row {i}) for language \"{code}\", which is not defined in any LocalizationModule — it never resolves.",
                                assetPath);
                    }
                }
            }
        }

        // Yields until the next editor tick. Uses EditorApplication.update rather than
        // Awaitable.NextFrameAsync because the player loop that drives NextFrameAsync does
        // not advance in Edit Mode, so awaiting a frame there never resumes.
        private static Awaitable EditorYieldAsync(CancellationToken cancellationToken)
        {
            var source = new AwaitableCompletionSource();

            void Tick()
            {
                EditorApplication.update -= Tick;
                if (cancellationToken.IsCancellationRequested)
                    source.SetCanceled();
                else
                    source.SetResult();
            }

            EditorApplication.update += Tick;
            return source.Awaitable;
        }
    }
}
