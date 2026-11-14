using System.Security.Claims;
using System.Text.RegularExpressions;
using AzureDesignStudio.Services.Auth;
using Grpc.Core;

namespace AzureDesignStudio.Services
{
    public static class ServiceTools
    {
        /// <summary>Where a host puts the caller of a call: the server's interceptor copies the
        /// authenticated HttpContext user here, the desktop's in-process invoker the signed-in
        /// user. The services read the caller from here only, so they need no web host.</summary>
        public const string UserStateKey = "ads.user";

        /// <summary>Where a host that serves exactly one user (the Desktop app, through its
        /// in-process invoker) says so. Read from <c>UserState</c> only and counted only when the
        /// stored value is the boolean true: no claim, header or request field can declare it.</summary>
        public const string SingleUserHostKey = "ads.host.singleuser";

        /// <summary>The message of an in-band 401 for a token without a usable oid and tid.</summary>
        public const string IdentityInvalidMessage = "IDENTITY_INVALID: the token carries no usable oid and tid.";

        /// <summary>The caller of this call, or an anonymous principal when the host set none.</summary>
        public static ClaimsPrincipal GetUser(ServerCallContext context) =>
            context.UserState.TryGetValue(UserStateKey, out var user) && user is ClaimsPrincipal principal
                ? principal
                : new ClaimsPrincipal(new ClaimsIdentity());

        /// <summary>The typed caller: identity from the validated principal, the single-user
        /// marker from the host. False, with a log-safe reason, when the principal carries no
        /// usable object id or tenant id.</summary>
        public static bool TryGetCaller(ServerCallContext context, out AdsCaller caller, out string failure)
        {
            var user = GetUser(context);
            if (!AdsIdentity.TryFrom(user, out var identity, out failure))
            {
                caller = null!;
                return false;
            }

            var singleUser = context.UserState.TryGetValue(SingleUserHostKey, out var marker) && marker is true;
            caller = new AdsCaller(identity, singleUser, user.FindFirst("name")?.Value);
            return true;
        }

        /// <summary>A setting that holds a real value: not empty and not one of the
        /// "&lt;kv name&gt;" style placeholders the checked-in appsettings.json carries.</summary>
        public static bool IsConfigured(string? value) =>
            !string.IsNullOrWhiteSpace(value) && !value.Contains('<') && !value.Contains('>');

        public static bool IsValidName(string name)
        {
            Regex regex = new(@"^[a-zA-Z0-9\s]+$"); // Alphanumeric characters and whitespace
            if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || !regex.IsMatch(name))
                return false;

            return true;
        }

        public static bool IsValidSubscriptionName(string subscriptionName)
        {
            Regex regex = new(@"^[^<>;|]+$"); // Azure subscription name cannot have these characters: < > ; |
            if (string.IsNullOrWhiteSpace(subscriptionName) || subscriptionName.Length > 64
                || !regex.IsMatch(subscriptionName))
                return false;

            return true;
        }
    }
}
