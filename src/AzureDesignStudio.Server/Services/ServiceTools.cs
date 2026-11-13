using Grpc.Core;
using System.Text.RegularExpressions;

namespace AzureDesignStudio.Server.Services
{
    public static class ServiceTools
    {
        private const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";

        /// <summary>The caller's Entra object id, or null when the token carries none that
        /// parses as a GUID. Every caller builds a <see cref="Guid"/> from the result, so a
        /// malformed claim has to read as "no identity" (401) here rather than surface as an
        /// unhandled <see cref="FormatException"/> in the RPC. The claim text is returned
        /// unchanged: stored approvals compare against it ordinally.</summary>
        internal static string? GetUserId(ServerCallContext context) =>
            GetUserId(context.GetHttpContext().User);

        internal static string? GetUserId(System.Security.Claims.ClaimsPrincipal user)
        {
            // "oid" is what arrives when inbound claim mapping is switched off.
            var value = user.FindFirst(ObjectIdClaim)?.Value ?? user.FindFirst("oid")?.Value;
            return Guid.TryParse(value, out _) ? value : null;
        }

        /// <summary>A setting that holds a real value: not empty and not one of the
        /// "&lt;kv name&gt;" style placeholders the checked-in appsettings.json carries.</summary>
        internal static bool IsConfigured(string? value) =>
            !string.IsNullOrWhiteSpace(value) && !value.Contains('<') && !value.Contains('>');

        internal static bool IsValidName(string name)
        {
            Regex regex = new(@"^[a-zA-Z0-9\s]+$"); // Alphanumeric characters and whitespace
            if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || !regex.IsMatch(name))
                return false;

            return true;
        }

        internal static bool IsValidSubscriptionName(string subscriptionName)
        {
            Regex regex = new(@"^[^<>;|]+$"); // Azure subscription name cannot have these characters: < > ; |
            if (string.IsNullOrWhiteSpace(subscriptionName) || subscriptionName.Length > 64
                || !regex.IsMatch(subscriptionName))
                return false;

            return true;
        }
    }
}
