namespace Molca.Editor.KnowledgeGraph
{
    /// <summary>
    /// Fork/add-on extension point: lets a package contribute its own type-graph sections to the
    /// exported Unity facts corpus (<see cref="UnityFactsExporter"/>) without modifying Core. Mirrors
    /// <see cref="Molca.Editor.FrameworkGraph.IFrameworkGraphContributor"/>'s discovery contract:
    /// implement this in an editor-only class (parameterless constructor); <see cref="UnityFactsExporter"/>
    /// discovers every implementor via <c>TypeCache</c> and calls <see cref="Contribute"/> after Core's own
    /// built-in type sections (RuntimeSubsystems, Settings Modules, …) are written.
    /// </summary>
    /// <remarks>
    /// Contract for implementers:
    /// <list type="bullet">
    /// <item>Use <see cref="UnityFactsExporter.WriteTypeSection"/> to append a heading + one entry per
    /// type, matching the markdown shape of Core's own sections so graphify's extractor treats them
    /// uniformly.</item>
    /// <item>Read-only: this only reads <c>TypeCache</c>/reflection and appends markdown; it never
    /// touches assets or scenes.</item>
    /// <item>Guard your own reads; the exporter also wraps each contributor in try/catch so one faulting
    /// contributor cannot break the export.</item>
    /// </list>
    /// Placement: an editor-only assembly in the contributing package (e.g. <c>com.molca.sequence</c>'s
    /// <c>Editor/Integrations/</c>).
    /// </remarks>
    public interface IKnowledgeGraphContributor
    {
        /// <summary>
        /// Appends this contributor's markdown type section(s) to <paramref name="markdown"/>.
        /// </summary>
        /// <param name="markdown">The shared corpus buffer being built for <c>molca-types.md</c>.</param>
        /// <returns>The number of types described, added to the exporter's running total.</returns>
        int Contribute(System.Text.StringBuilder markdown);
    }
}
