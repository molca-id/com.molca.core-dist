namespace Molca.Localization
{
    /// <summary>
    /// Compatibility name for the v1 localization value. New fields should use
    /// <see cref="LocalizedValue"/>; this adapter retains every existing public member and serialized
    /// v1 field through inheritance.
    /// </summary>
    [System.Serializable]
    public class DynamicLocalization : LocalizedValue
    {
    }
}
