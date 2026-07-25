using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Tabular
{
    /// <summary>
    /// Discovers every <see cref="ITabularReader"/> in the loaded assemblies via
    /// <see cref="TypeCache"/> and resolves the one that handles a given file extension. This is the
    /// "managed add-on" surface for tabular formats: Core's <see cref="CsvTabularReader"/> and any reader
    /// shipped by an installed add-on package are found the same way, with no registration list to maintain.
    /// </summary>
    /// <remarks>
    /// Discovery is lazy and cached for the domain; the cache resets automatically on domain reload (static
    /// state is cleared). A reader whose constructor throws, or that is abstract/generic/has no public
    /// parameterless constructor, is skipped with a warning so one malformed add-on cannot break CSV. When
    /// two readers claim the same extension the first by ordinal type name wins (deterministic) and a warning
    /// is logged. Editor-only.
    /// </remarks>
    public static class TabularReaderRegistry
    {
        private static List<ITabularReader> _readers;
        // extension (lower-case, dot-prefixed) → chosen reader.
        private static Dictionary<string, ITabularReader> _byExtension;

        /// <summary>
        /// Resolves the reader for <paramref name="path"/>'s extension.
        /// </summary>
        /// <param name="path">A file path; only its extension is inspected.</param>
        /// <param name="reader">The matching reader, or null when no installed reader handles the extension.</param>
        /// <returns>
        /// True when a reader was found. False signals graceful absence (e.g. the XLSX add-on is not
        /// installed) — callers should return a helpful message listing <see cref="SupportedExtensions"/>,
        /// never throw.
        /// </returns>
        public static bool TryGetReader(string path, out ITabularReader reader)
        {
            reader = null;
            EnsureDiscovered();
            var ext = Path.GetExtension(path ?? string.Empty);
            if (string.IsNullOrEmpty(ext)) return false;
            return _byExtension.TryGetValue(ext.ToLowerInvariant(), out reader);
        }

        /// <summary>All extensions any installed reader claims, sorted, for error messages and schema docs.</summary>
        public static IReadOnlyList<string> SupportedExtensions
        {
            get
            {
                EnsureDiscovered();
                return _byExtension.Keys.OrderBy(e => e, StringComparer.Ordinal).ToList();
            }
        }

        /// <summary>
        /// The installed readers and the extensions each claims — feeds diagnostics and a future Hub
        /// "Add-ons" panel that lists which tabular capabilities are live.
        /// </summary>
        public static IReadOnlyList<(string readerType, IReadOnlyList<string> extensions)> Describe()
        {
            EnsureDiscovered();
            return _readers
                .Select(r => (
                    r.GetType().Name,
                    (IReadOnlyList<string>)SafeExtensions(r).OrderBy(e => e, StringComparer.Ordinal).ToList()))
                .OrderBy(t => t.Item1, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Forces re-discovery on the next query. Primarily for tests.</summary>
        public static void Invalidate()
        {
            _readers = null;
            _byExtension = null;
        }

        private static void EnsureDiscovered()
        {
            if (_readers != null) return;

            _readers = new List<ITabularReader>();
            _byExtension = new Dictionary<string, ITabularReader>(StringComparer.OrdinalIgnoreCase);

            foreach (var type in TypeCache.GetTypesDerivedFrom<ITabularReader>()
                         .OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (type.IsAbstract || type.IsGenericTypeDefinition || type.IsInterface) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    Debug.LogWarning($"[Molca] ITabularReader '{type.FullName}' has no public parameterless " +
                                     "constructor; skipping.");
                    continue;
                }

                ITabularReader reader;
                try { reader = (ITabularReader)Activator.CreateInstance(type); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Molca] ITabularReader '{type.FullName}' failed to instantiate: {ex.Message}");
                    continue;
                }

                _readers.Add(reader);
                foreach (var ext in SafeExtensions(reader))
                {
                    var key = ext.ToLowerInvariant();
                    if (_byExtension.TryGetValue(key, out var existing))
                    {
                        Debug.LogWarning($"[Molca] Tabular extension '{key}' is claimed by both " +
                                         $"'{existing.GetType().Name}' and '{reader.GetType().Name}'; " +
                                         $"keeping '{existing.GetType().Name}'.");
                        continue;
                    }
                    _byExtension[key] = reader;
                }
            }
        }

        private static IEnumerable<string> SafeExtensions(ITabularReader reader)
        {
            IEnumerable<string> exts;
            try { exts = reader.SupportedExtensions; }
            catch { exts = null; }

            var result = new List<string>();
            if (exts != null)
                foreach (var e in exts)
                    if (!string.IsNullOrWhiteSpace(e))
                        result.Add(e.StartsWith(".") ? e : "." + e);
            return result;
        }
    }
}
