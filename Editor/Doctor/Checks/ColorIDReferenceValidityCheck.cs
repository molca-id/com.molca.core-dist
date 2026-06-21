using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Molca.ColorID;
using Molca.Settings;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Validates serialized <see cref="ColorIDReference"/> values in prefabs,
    /// ScriptableObjects, and currently open scenes against the swatches/color ids
    /// defined in the project's <see cref="ColorModule"/> assets. A reference whose
    /// <c>swatch.colorId</c> pair is not defined resolves to magenta at runtime
    /// (see <c>ColorModule.GetColorCore</c>); a blank reference silently falls back
    /// to the field defaults.
    /// </summary>
    /// <remarks>
    /// Keys from every <see cref="ColorModule"/> asset are unioned: a project may ship
    /// several palettes (color schemes) and the active one is a runtime decision, so a
    /// reference is treated as valid when <em>any</em> module defines its key. This keeps
    /// the check conservative — it only reports pairs that are missing everywhere.
    ///
    /// Closed scenes are not opened (too invasive for a validation pass); run the check
    /// with the relevant scenes open. The check stays on the main thread because
    /// AssetDatabase, SerializedObject, and SceneManager are main-thread only, and yields
    /// before each heavy asset so a large project stays responsive and cancellable.
    /// </remarks>
    public class ColorIDReferenceValidityCheck : IDoctorCheck
    {
        public string Id => "color-id-reference-invalid";
        public string Description => "ColorIDReference swatch/colorId pairs not defined in any ColorModule";

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            await Awaitable.MainThreadAsync();
            var issues = new List<DoctorIssue>();

            var modules = AssetDatabase.FindAssets("t:ColorModule")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<ColorModule>)
                .Where(m => m != null)
                .ToList();

            if (modules.Count == 0)
            {
                issues.Add(new DoctorIssue(Id, DoctorSeverity.Info,
                    "No ColorModule asset found — ColorIDReference values cannot be validated."));
                return issues;
            }

            // Build the valid sets directly from the serialized swatch lists rather than
            // the runtime _colorCache, which is only populated after BuildFromDefinitions
            // runs. This mirrors ColorModule.HasColorCore's composite-key lookup
            // ("{swatch}.{colorId}") without mutating the asset.
            var validSwatches = new HashSet<string>();
            var validKeys = new HashSet<string>();
            foreach (var module in modules)
            {
                foreach (var swatch in module.ColorSwatches ?? Enumerable.Empty<ColorModule.ColorSwatch>())
                {
                    if (swatch == null || string.IsNullOrEmpty(swatch.SwatchName))
                        continue;
                    validSwatches.Add(swatch.SwatchName);
                    foreach (var def in swatch.ColorDefinitions ?? Enumerable.Empty<ColorModule.ColorDefinition>())
                    {
                        if (def != null && !string.IsNullOrEmpty(def.ColorId))
                            validKeys.Add($"{swatch.SwatchName}.{def.ColorId}");
                    }
                }

                // Un-migrated assets may still carry legacy definitions, which resolve
                // through the Default swatch at runtime.
                foreach (var def in module.ColorDefinitions ?? Enumerable.Empty<ColorModule.ColorDefinition>())
                {
                    if (def != null && !string.IsNullOrEmpty(def.ColorId))
                    {
                        validSwatches.Add("Default");
                        validKeys.Add($"Default.{def.ColorId}");
                    }
                }
            }

            // Prefabs and ScriptableObjects across the project, plus components in open scenes.
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
                issues.AddRange(ScanObjects(targets, path, validSwatches, validKeys));
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
                issues.AddRange(ScanObjects(behaviours, scene.path ?? scene.name, validSwatches, validKeys));
            }

            return issues;
        }

        private IEnumerable<DoctorIssue> ScanObjects(
            IEnumerable<Object> objects, string assetPath,
            HashSet<string> validSwatches, HashSet<string> validKeys)
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

                    // ColorIDReference serializes as a generic struct with these two
                    // string children (see ColorIDReference._swatchName / _colorId).
                    var swatchProp = property.FindPropertyRelative("_swatchName");
                    var colorIdProp = property.FindPropertyRelative("_colorId");
                    if (swatchProp == null || colorIdProp == null
                        || swatchProp.propertyType != SerializedPropertyType.String
                        || colorIdProp.propertyType != SerializedPropertyType.String)
                        continue;

                    enterChildren = false; // it's a ColorIDReference; don't descend into it
                    var swatch = swatchProp.stringValue;
                    var colorId = colorIdProp.stringValue;

                    if (string.IsNullOrEmpty(swatch) || string.IsNullOrEmpty(colorId))
                    {
                        yield return new DoctorIssue(Id, DoctorSeverity.Warning,
                            $"ColorIDReference `{property.propertyPath}` on {obj.name} has a blank swatch or colorId — it will fall back to the field defaults.",
                            assetPath);
                        continue;
                    }

                    if (validKeys.Contains($"{swatch}.{colorId}"))
                        continue;

                    var detail = validSwatches.Contains(swatch)
                        ? $"colorId \"{colorId}\" is not defined in swatch \"{swatch}\""
                        : $"swatch \"{swatch}\" is not defined in any ColorModule";

                    yield return new DoctorIssue(Id, DoctorSeverity.Error,
                        $"ColorIDReference `{property.propertyPath}` on {obj.name} — {detail}. It resolves to magenta at runtime.",
                        assetPath);
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
