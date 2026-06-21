using UnityEngine;

namespace Molca.Networking.Auth
{
    /// <summary>
    /// Defines the contract for authentication user management.
    /// </summary>
    public interface IAuthUser
    {
        /// <summary>
        /// Gets the current authentication token.
        /// </summary>
        string Token { get; }

        /// <summary>
        /// Gets the user data associated with the authenticated user.
        /// </summary>
        IAuthUserData Data { get; }

        /// <summary>
        /// Deserializes user data from a JSON string.
        /// </summary>
        /// <param name="json">The JSON string containing user data.</param>
        /// <returns>True if deserialization was successful, false otherwise.</returns>
        bool DeserializeFromJson(string json);

        /// <summary>
        /// Creates a JSON string for login credentials.
        /// </summary>
        /// <param name="username">The username for login.</param>
        /// <param name="password">The password for login.</param>
        /// <returns>A JSON string containing login credentials.</returns>
        string GetLoginJson(string username, string password);

        /// <summary>
        /// Gets the unique identifier for the current user.
        /// </summary>
        /// <returns>The user's unique identifier.</returns>
        string GetUserId();

        /// <summary>
        /// Clears all user data and authentication information.
        /// </summary>
        void Clear();
    }

    /// <summary>
    /// Defines the contract for user data associated with an authenticated user.
    /// </summary>
    public interface IAuthUserData
    {
        /// <summary>
        /// Gets the unique identifier for the user.
        /// </summary>
        string UserId { get; }
    }

    /// <summary>
    /// Base implementation of IAuthUser that provides common functionality.
    /// </summary>
    public abstract class AuthUser : MonoBehaviour, IAuthUser
    {
        public string Token { get; protected set; }
        public IAuthUserData Data { get; protected set; }

        /// <summary>
        /// The current refresh token, or <c>null</c> when the user model does not carry
        /// one. The base returns <c>null</c>; a subclass whose auth payload includes a
        /// refresh token overrides this so <see cref="AuthManager.RefreshAsync"/> can
        /// renew an expired access token without a full re-login. When <c>null</c>,
        /// refresh is unavailable and the original failure surfaces to the caller.
        /// </summary>
        public virtual string RefreshToken => null;

        /// <summary>
        /// Deserializes user data from a JSON string. Call state changed event if successful.
        /// </summary>
        /// <param name="json"></param>
        /// <returns></returns>
        public abstract bool DeserializeFromJson(string json);
        public abstract string GetLoginJson(string username, string password);
        public abstract bool IsGuest { get; }
        
        /// <summary>
        /// Gets the user ID from the Data property if available.
        /// </summary>
        /// <returns>The user ID or null if Data is not available.</returns>
        public virtual string GetUserId() => Data?.UserId;
        
        /// <summary>
        /// Clears authentication token and user data.
        /// </summary>
        public virtual void Clear()
        {
            Token = null;
            Data = null;
            AuthEvents.StateChanged.Dispatch(new AuthChangedEventData(false));
        }

        internal void SetData(IAuthUserData data)
        {
            Data = data;
            AuthEvents.StateChanged.Dispatch(new AuthChangedEventData(true));
        }
    }
}