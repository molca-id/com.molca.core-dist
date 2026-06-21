using System.Collections.Generic;

namespace Molca.Audio
{
    /// <summary>
    /// Common interface for audio collections to work with AudioLibrary
    /// </summary>
    public interface IAudioCollection
    {
        string CollectionName { get; }
        string Description { get; }
        string AddressableGroupName { get; }

        void Initialize();
        void Clear();
        string[] GetAllAudioIds();
    }
} 