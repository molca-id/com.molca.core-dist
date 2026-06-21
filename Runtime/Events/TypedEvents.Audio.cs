namespace Molca.Events
{
    /// <summary>
    /// Audio system events
    /// </summary>
    public static partial class TypedEvents
    {
        // Audio events
        public static readonly Event<float> MasterVolumeChanged = new Event<float>(EventConstants.Audio.MasterVolumeChanged);
        public static readonly Event<float> MusicVolumeChanged = new Event<float>(EventConstants.Audio.MusicVolumeChanged);
        public static readonly Event<float> SfxVolumeChanged = new Event<float>(EventConstants.Audio.SfxVolumeChanged);
        public static readonly Event<float> VoiceVolumeChanged = new Event<float>(EventConstants.Audio.VoiceVolumeChanged);
    }
}
