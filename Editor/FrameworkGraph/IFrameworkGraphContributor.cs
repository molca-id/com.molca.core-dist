namespace Molca.Editor.FrameworkGraph
{
    /// <summary>
    /// Fork extension point (Sprint 22.8): lets an SDK layer contribute its own <em>read-only</em> nodes
    /// and edges to the Framework Graph without modifying Core. Implement this in an editor-only class in
    /// your fork (parameterless constructor); <see cref="FrameworkGraphBuilder"/> discovers every
    /// implementor via <c>TypeCache</c> and calls <see cref="Contribute"/> after the Core layers are built.
    /// </summary>
    /// <remarks>
    /// Contract for fork authors:
    /// <list type="bullet">
    /// <item>Add nodes with <see cref="FrameworkNodeCategory.Fork"/> and namespace your ids
    /// (e.g. <c>"vr:hand-tracking"</c>) so they never collide with Core's.</item>
    /// <item>Read-only: describe state via <see cref="FrameworkGraphSnapshot.AddNode"/>/<see cref="FrameworkGraphSnapshot.AddEdge"/>;
    /// never mutate serialized data here — routing to the guarded action tools is the only write path.</item>
    /// <item>Honour the SOs-out boundary: a ScriptableObject may appear as a config node, never as a
    /// runtime-resolvable scene reference target.</item>
    /// <item>Guard your own reads; the builder also wraps each contributor in try/catch so one faulting
    /// contributor cannot break the graph.</item>
    /// </list>
    /// Placement: <c>Assets/_MolcaSDK/[Layer]/Editor/</c> (or the fork's editor assembly).
    /// </remarks>
    public interface IFrameworkGraphContributor
    {
        /// <summary>Adds this contributor's read-only nodes/edges to the snapshot under construction.</summary>
        void Contribute(FrameworkGraphSnapshot snapshot);
    }
}
