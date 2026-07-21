using System.Collections.Generic;

namespace Molca.Editor.Hub.Docs
{
    /// <summary>
    /// One reference guide contributed to the Molca Hub docs browser: an identity, display metadata, and the
    /// absolute path to its Markdown source on disk.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/Docs/</c>. Immutable descriptor produced by a
    /// <see cref="MolcaDocsProvider"/> and resolved by <see cref="MolcaDocsRegistry"/>. Metadata comes from
    /// the doc's YAML front-matter (see <see cref="MolcaMarkdown.StripFrontMatter"/>).
    /// </remarks>
    public sealed class MolcaDocEntry
    {
        /// <summary>Stable identity used for de-duplication and <c>molca://doc/&lt;id&gt;</c> deep-links.</summary>
        public string Id { get; }

        /// <summary>Human-readable title shown in the rail and the doc header.</summary>
        public string Title { get; }

        /// <summary>Grouping category (the rail parent this doc nests under).</summary>
        public string Category { get; }

        /// <summary>Sort order within the category (ascending); ties break on <see cref="Title"/>.</summary>
        public int Order { get; }

        /// <summary>Absolute filesystem path to the Markdown source (read on demand for rendering).</summary>
        public string AbsolutePath { get; }

        /// <summary>Name of the package that owns this doc (for provenance/diagnostics), or <c>null</c>.</summary>
        public string OwnerPackage { get; }

        /// <summary>
        /// Human-readable product/documentation-set this doc belongs to (the top-level grouping shown in the
        /// docs browser's product switcher), or <c>null</c> to let the registry derive one from
        /// <see cref="OwnerPackage"/>. Sourced from the doc's <c>product</c> front-matter when present, else
        /// the owning package's display name.
        /// </summary>
        public string Product { get; }

        /// <summary>Creates a doc entry.</summary>
        public MolcaDocEntry(string id, string title, string category, int order, string absolutePath,
            string ownerPackage = null, string product = null)
        {
            Id = id;
            Title = title;
            Category = string.IsNullOrWhiteSpace(category) ? "Reference" : category;
            Order = order;
            AbsolutePath = absolutePath;
            OwnerPackage = ownerPackage;
            Product = product;
        }
    }

    /// <summary>A resolved rail category: an ordered set of docs grouped under one heading.</summary>
    public sealed class MolcaDocCategory
    {
        /// <summary>The category display name (rail parent label).</summary>
        public string Name { get; }

        /// <summary>Category sort order (ascending); derived from the lowest doc order in the group.</summary>
        public int Order { get; }

        /// <summary>The docs in this category, already sorted by order then title.</summary>
        public IReadOnlyList<MolcaDocEntry> Docs { get; }

        /// <summary>Creates a resolved category.</summary>
        public MolcaDocCategory(string name, int order, IReadOnlyList<MolcaDocEntry> docs)
        {
            Name = name;
            Order = order;
            Docs = docs;
        }
    }

    /// <summary>
    /// A resolved product group: one documentation set (Core, an SDK layer, a fork, or the project itself),
    /// holding its category tree. The top-level grouping the docs browser's product switcher pivots on.
    /// </summary>
    public sealed class MolcaDocProduct
    {
        /// <summary>Stable grouping key — the owning package name, or <c>"project"</c> for un-owned docs.</summary>
        public string Key { get; }

        /// <summary>Product display label shown in the switcher.</summary>
        public string Label { get; }

        /// <summary>Sort order among products (ascending): Core first, then SDK, other packages, project last.</summary>
        public int Order { get; }

        /// <summary>The category tree scoped to this product, in resolved order.</summary>
        public IReadOnlyList<MolcaDocCategory> Categories { get; }

        /// <summary>Creates a resolved product group.</summary>
        public MolcaDocProduct(string key, string label, int order, IReadOnlyList<MolcaDocCategory> categories)
        {
            Key = key;
            Label = label;
            Order = order;
            Categories = categories;
        }
    }

    /// <summary>
    /// Editor-only seam for contributing reference docs to the Molca Hub docs browser. Subclass and return
    /// one or more <see cref="MolcaDocEntry"/>; non-abstract subclasses are discovered automatically via
    /// <c>TypeCache</c> by <see cref="MolcaDocsRegistry"/>.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/Docs/</c>. Base class for the fork extension point —
    /// an SDK layer or project adds docs by shipping a provider (or, for the common case, by dropping
    /// Markdown files that Core's built-in <see cref="MolcaCoreDocsProvider"/> already scans), never by
    /// editing Core. Subclasses must have a public parameterless constructor. <see cref="GetDocs"/> runs on
    /// the main thread during Hub construction; a provider that throws is logged and skipped.
    /// </remarks>
    public abstract class MolcaDocsProvider
    {
        /// <summary>Returns the docs this provider contributes.</summary>
        public abstract IEnumerable<MolcaDocEntry> GetDocs();
    }
}
