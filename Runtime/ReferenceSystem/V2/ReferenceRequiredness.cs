namespace Molca.ReferenceSystem
{
    /// <summary>
    /// How much a reference site depends on its target actually resolving.
    /// </summary>
    /// <remarks>
    /// V1 had no such declaration, so every unset reference was equally legal and nothing could be
    /// validated before play. A field the author forgot to wire and a field deliberately left empty
    /// were indistinguishable, which is why broken wiring surfaced as a null at runtime rather than
    /// as an error in the editor.
    /// </remarks>
    public enum ReferenceRequiredness
    {
        /// <summary>
        /// Unresolved is a legitimate state. Inspectable, and silent at runtime by default.
        /// </summary>
        Optional = 0,

        /// <summary>
        /// Must resolve. Unset or unresolvable is an editor and build error, and a required runtime
        /// resolve throws.
        /// </summary>
        Required = 1,

        /// <summary>
        /// Must resolve eventually. The target may register after the owner starts, so only a
        /// timeout or the end of the owner's lifecycle turns it into an error.
        /// </summary>
        DeferredRequired = 2,
    }

    /// <summary>
    /// When the target of a reference is expected to be available.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ReferenceRequiredness"/> on purpose: "this must resolve" and "this
    /// may take a while to arrive" are independent, and collapsing them is what forces validation to
    /// either accept everything or reject legitimate deferred wiring.
    /// </remarks>
    public enum ReferenceAvailabilityPolicy
    {
        /// <summary>
        /// The provider must already exist when the owner becomes active. The safest default for a
        /// reference within one scene.
        /// </summary>
        Immediate = 0,

        /// <summary>
        /// The provider may arrive during an explicit bounded wait. V1 data migrates to this,
        /// because v1's resolve path waited unconditionally — but the Hub labels it inferred rather
        /// than authored, since nobody chose it.
        /// </summary>
        Deferred = 1,

        /// <summary>
        /// The reference is only expected to resolve under a named scene load set or feature
        /// condition. Outside that condition, unresolved is not a finding.
        /// </summary>
        Conditional = 2,
    }
}
