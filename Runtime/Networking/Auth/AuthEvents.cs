using Molca.Events;

namespace Molca.Networking.Auth
{
    public static class AuthEvents
    {
        private const string AuthStateChanged = "Auth.StateChanged";
        private const string AuthLoggedIn = "Auth.LoggedIn";
        private const string AuthLoggedOut = "Auth.LoggedOut";
        private const string AuthExpired = "Auth.Expired";

        public static readonly TypedEvents.Event<AuthChangedEventData> StateChanged = new TypedEvents.Event<AuthChangedEventData>(AuthStateChanged);
        public static readonly TypedEvents.Event<AuthLoggedInEventData> LoggedIn = new TypedEvents.Event<AuthLoggedInEventData>(AuthLoggedIn);
        public static readonly TypedEvents.Event<AuthLoggedOutEventData> LoggedOut = new TypedEvents.Event<AuthLoggedOutEventData>(AuthLoggedOut);

        /// <summary>
        /// Raised when an access token has expired and could not be refreshed (no
        /// refresh token, or the refresh endpoint rejected it). The session is no longer
        /// valid; listeners should route the user back to login.
        /// </summary>
        public static readonly TypedEvents.Event<AuthExpiredEventData> Expired = new TypedEvents.Event<AuthExpiredEventData>(AuthExpired);
    }

    public class AuthChangedEventData : EventData
    {
        public bool IsAuthenticated { get; }

        public AuthChangedEventData(bool isAuthenticated)
        {
            IsAuthenticated = isAuthenticated;
        }
    }

    public class AuthLoggedInEventData : EventData
    {
        public string UserId { get; }

        public AuthLoggedInEventData(string userId)
        {
            UserId = userId;
        }
    }

    public class AuthLoggedOutEventData : EventData
    {
        public string UserId { get; }

        public AuthLoggedOutEventData(string userId)
        {
            UserId = userId;
        }
    }

    public class AuthExpiredEventData : EventData
    {
        public string UserId { get; }

        public AuthExpiredEventData(string userId)
        {
            UserId = userId;
        }
    }
}
