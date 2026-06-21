namespace Molca.Events
{
    /// <summary>
    /// Scene management events
    /// </summary>
    public static partial class TypedEvents
    {
        // Scene events
        public static readonly Event<SceneLoadEventData> SceneLoadStarted = new Event<SceneLoadEventData>(EventConstants.Scene.LoadStarted);
        public static readonly Event<SceneLoadEventData> SceneLoadCompleted = new Event<SceneLoadEventData>(EventConstants.Scene.LoadCompleted);
        public static readonly Event<SceneLoadErrorEventData> SceneLoadFailed = new Event<SceneLoadErrorEventData>(EventConstants.Scene.LoadFailed);
        public static readonly Event<string> SceneUnloadStarted = new Event<string>(EventConstants.Scene.UnloadStarted);
        public static readonly Event<string> SceneUnloadCompleted = new Event<string>(EventConstants.Scene.UnloadCompleted);
    }
}
