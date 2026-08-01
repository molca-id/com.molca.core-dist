namespace Molca.ReferenceSystem
{
    /// <summary>
    /// The space within which a reference id is required to be unique.
    /// </summary>
    /// <remarks>
    /// V1 had exactly one implicit scope — every id had to be unique across the entire project —
    /// which is why placing a referenceable prefab twice was a conflict, and why the editor
    /// regenerated ids on placement to paper over it. Making scope part of identity is what lets two
    /// instances of the same prefab carry the same authored local id without colliding.
    /// </remarks>
    public enum ReferenceScopeKind
    {
        /// <summary>
        /// Compatibility representation of existing <c>(RefType, RefId)</c> data, which carries no
        /// authored scope.
        /// </summary>
        /// <remarks>
        /// Deliberately the zero value. A default-constructed key, or one deserialized from data
        /// written before scopes existed, must land on the compatibility path — which tolerates a
        /// missing scope and reports what it did — rather than silently claiming to be an exact
        /// <see cref="Global"/> identity it was never authored as.
        /// </remarks>
        LegacyGlobal = 0,

        /// <summary>
        /// Unique across every simultaneously loaded provider. For true application singletons.
        /// </summary>
        Global = 1,

        /// <summary>
        /// Unique within one authored scene. Resolving across scenes requires an explicit target
        /// scene and an availability policy.
        /// </summary>
        Scene = 2,

        /// <summary>
        /// Unique within one runtime prefab instance, delimited by the nearest
        /// <see cref="ReferenceScopeRoot"/>. Two instances of the same prefab may carry identical
        /// local ids without conflict.
        /// </summary>
        PrefabLocal = 3,
    }
}
