using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Localization;
using UnityEngine;
using UnityEngine.Localization.Pseudo;

namespace Molca.Editor
{
    public enum LocalizationPseudoProfile
    {
        AccentExpansion,
        MissingKeyVisibility,
        RightToLeftStress,
    }

    public sealed class LocalizationPseudoCatalogRow
    {
        public string CollectionId { get; internal set; }
        public string Key { get; internal set; }
        public string LocaleCode { get; internal set; }
        public string Source { get; internal set; }
        public string Pseudo { get; internal set; }
    }

    public sealed class LocalizationPseudoOverflow
    {
        public string Path { get; internal set; }
        public string Source { get; internal set; }
        public string Pseudo { get; internal set; }
        public Vector2 Available { get; internal set; }
        public Vector2 Preferred { get; internal set; }
    }

    /// <summary>Non-mutating pseudo-localization and loaded-UI overflow diagnostics.</summary>
    public static class LocalizationPseudoPreviewService
    {
        public static string Transform(string value, LocalizationPseudoProfile profile)
        {
            value ??= string.Empty;
            if (profile == LocalizationPseudoProfile.MissingKeyVisibility)
                return $"⟦MISSING:{(value.Length == 0 ? "<empty>" : value)}⟧";

            var locale = PseudoLocale.CreatePseudoLocale();
            try
            {
                if (profile == LocalizationPseudoProfile.RightToLeftStress)
                {
                    locale.Methods.Clear();
                    locale.Methods.Add(new PreserveTags());
                    locale.Methods.Add(new Mirror());
                    locale.Methods.Add(new Encapsulator());
                }
                return locale.GetPseudoString(value);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(locale);
            }
        }

        public static IReadOnlyList<LocalizationPseudoCatalogRow> PreviewCatalog(
            LocalizationPseudoProfile profile,
            string collectionId = null,
            int maximum = 100)
        {
            maximum = Mathf.Clamp(maximum, 1, 1000);
            return LocalizationCatalogAuthoringService.Capture().Cells
                .Where(cell => string.IsNullOrWhiteSpace(collectionId) ||
                               string.Equals(cell.CollectionId, collectionId, StringComparison.Ordinal))
                .Take(maximum)
                .Select(cell => new LocalizationPseudoCatalogRow
                {
                    CollectionId = cell.CollectionId,
                    Key = cell.Key,
                    LocaleCode = cell.LocaleCode,
                    Source = cell.Value,
                    Pseudo = Transform(cell.Value, profile),
                })
                .ToArray();
        }

        public static IReadOnlyList<LocalizationPseudoOverflow> ScanLoadedUi(
            LocalizationPseudoProfile profile)
        {
            var results = new List<LocalizationPseudoOverflow>();
            foreach (var root in GameObjectEditingService.EnumerateRoots())
            foreach (var localizedText in root.GetComponentsInChildren<LocalizedText>(true))
            {
                var source = localizedText.GetRenderedText();
                var pseudo = Transform(source, profile);
                if (!localizedText.WouldOverflow(
                        pseudo,
                        out var available,
                        out var preferred))
                    continue;
                results.Add(new LocalizationPseudoOverflow
                {
                    Path = GameObjectEditingService.GetHierarchyPath(localizedText.gameObject),
                    Source = source,
                    Pseudo = pseudo,
                    Available = available,
                    Preferred = preferred,
                });
            }
            return results;
        }
    }
}
