using UnityEngine;

namespace Molca.App.Preload
{
    public interface IPreloadCheck
    {
        Awaitable RunCheck();
    }
}
