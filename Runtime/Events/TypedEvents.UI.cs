namespace Molca.Events
{
    /// <summary>
    /// User interface events
    /// </summary>
    public static partial class TypedEvents
    {
        // UI events
        public static readonly Event<string> DialogShown = new Event<string>(EventConstants.UI.DialogShown);
        public static readonly Event<string> DialogHidden = new Event<string>(EventConstants.UI.DialogHidden);
        public static readonly Event<string> LanguageChanged = new Event<string>(EventConstants.UI.LanguageChanged);
    }
}
