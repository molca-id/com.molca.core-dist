using Molca.Networking.Auth;
using Molca.Utils;

namespace Molca.App.Auth
{
    public class SDKAuthUser : AuthUser
    {
        public override bool IsGuest => Data.UserId == "Guest";

        public override void Clear()
        {
            Data = null;
        }

        public override bool DeserializeFromJson(string json)
        {
            Data = JsonHelper.FromJson<SDKUserData>(json);
            return true;
        }

        public override string GetLoginJson(string username, string password)
        {
            return $"{{\"username\":\"{username}\",\"password\":\"{password}\"}}";
        }

        public override string GetUserId()
        {
            return Data?.UserId;
        }
    }
    
    public class SDKUserData : IAuthUserData
    {
        public string UserId { get; private set; }
        public string Username { get; private set; }

        public SDKUserData(string userId, string username)
        {
            UserId = userId;
            Username = username;
        }
    }
}