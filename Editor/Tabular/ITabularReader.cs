using System;
using System.Collections.Generic;

namespace Molca.Editor.Tabular
{
    /// <summary>
    /// Reads a tabular file (CSV, XLSX, …) into a neutral <see cref="TabularDocument"/>. This is the
    /// extension seam for the assistant's sheet tooling: Core ships a CSV/TSV reader, and additional
    /// formats are supplied by opt-in add-on packages that implement this interface.
    /// </summary>
    /// <remarks>
    /// Implementations are discovered by <see cref="TabularReaderRegistry"/> via
    /// <see cref="UnityEditor.TypeCache"/> — dropping in a package that references <c>Molca.Editor</c> and
    /// implements this interface adds a format with <em>no</em> registration step, no <c>#if</c>, and no
    /// asmdef define constraints. Because Core never references the add-on, a format is simply absent (not a
    /// compile error) when its package is not installed; every consumer must degrade gracefully — see
    /// <see cref="TabularReaderRegistry.TryGetReader"/>.
    /// <para>
    /// Implementations MUST have a public parameterless constructor and MUST be stateless: the registry
    /// instantiates one instance per type and reuses it. Editor-only.
    /// </para>
    /// </remarks>
    public interface ITabularReader
    {
        /// <summary>
        /// Lower-case file extensions this reader handles, each including the leading dot (e.g. <c>".csv"</c>).
        /// Matched case-insensitively against <c>Path.GetExtension</c>.
        /// </summary>
        IEnumerable<string> SupportedExtensions { get; }

        /// <summary>
        /// Reads <paramref name="path"/> into a document.
        /// </summary>
        /// <param name="path">Absolute, working-directory-relative, or project-relative path to an existing file.</param>
        /// <param name="options">Sheet selection, header handling, and row cap. See <see cref="TabularReadOptions"/>.</param>
        /// <returns>The parsed document; never null. An empty source yields zero rows.</returns>
        /// <exception cref="TabularReadException">
        /// Thrown for a missing/unreadable file, malformed content, or a requested sheet that does not exist.
        /// </exception>
        TabularDocument Read(string path, TabularReadOptions options = default);
    }

    /// <summary>
    /// Raised by an <see cref="ITabularReader"/> when a source cannot be read (missing file, malformed
    /// content, or an unknown sheet). Tool handlers translate this into a clean error payload rather than
    /// letting it surface as a transport-level exception.
    /// </summary>
    public sealed class TabularReadException : Exception
    {
        /// <summary>Creates the exception with a human-readable reason.</summary>
        public TabularReadException(string message) : base(message) { }

        /// <summary>Creates the exception with a reason and the underlying cause.</summary>
        public TabularReadException(string message, Exception inner) : base(message, inner) { }
    }
}
