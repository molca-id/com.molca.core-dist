using System;
using System.Collections.Generic;

namespace Molca.Localization
{
    /// <summary>Typed result from resolving a locale-keyed asset through the shared fallback graph.</summary>
    public readonly struct LocalizedAssetResolution<TReference>
    {
        public LocalizedAssetResolution(
            string requestedLocale,
            string resolvedLocale,
            TReference reference)
        {
            RequestedLocale = requestedLocale ?? string.Empty;
            ResolvedLocale = resolvedLocale ?? string.Empty;
            Reference = reference;
        }

        public string RequestedLocale { get; }
        public string ResolvedLocale { get; }
        public TReference Reference { get; }
        public bool Found => !EqualityComparer<TReference>.Default.Equals(Reference, default);
        public bool UsedFallback => Found && !string.Equals(
            RequestedLocale,
            ResolvedLocale,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Shared fallback traversal for localized strings, audio, and future asset sources.</summary>
    public static class LocalizationFallbackResolver
    {
        public static LocalizedAssetResolution<TReference> Resolve<TReference>(
            string requestedLocale,
            IEnumerable<string> fallbackChain,
            Func<string, TReference> getReference,
            Func<TReference, bool> isUsable)
        {
            if (getReference == null)
                throw new ArgumentNullException(nameof(getReference));
            if (isUsable == null)
                throw new ArgumentNullException(nameof(isUsable));

            foreach (var localeCode in fallbackChain ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(localeCode))
                    continue;
                var reference = getReference(localeCode);
                if (isUsable(reference))
                    return new LocalizedAssetResolution<TReference>(
                        requestedLocale,
                        localeCode,
                        reference);
            }

            return new LocalizedAssetResolution<TReference>(
                requestedLocale,
                string.Empty,
                default);
        }
    }
}
