using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;

namespace AzureDesignStudio.Services.Auth
{
    /// <summary>
    /// Who is calling: the user, the tenant and the scope their data is shared in, derived from
    /// the validated principal in this one place. Pure <see cref="System.Security.Claims"/> code
    /// with no ASP.NET Core type: the Desktop host runs it too, and Mac Catalyst has no ASP.NET
    /// Core. The subject claim is never read, whatever spelling it arrives in: it is unique per
    /// application, not per user.
    /// </summary>
    public sealed record AdsIdentity(
        Guid UserId, Guid TenantId, Guid ScopeId, bool IsPersonalAccount, IReadOnlySet<string> Roles)
    {
        /// <summary>Every personal Microsoft account signs in to this one tenant.</summary>
        public static readonly Guid ConsumerTenantId = new("9188040d-6c67-4c5b-b112-36a304b66dad");

        public const string GuardrailAdminRole = "Guardrail.Admin";
        public const string DeployApproverRole = "Deploy.Approver";

        public const string ObjectIdUriClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";
        public const string ObjectIdShortClaim = "oid";
        public const string TenantIdUriClaim = "http://schemas.microsoft.com/identity/claims/tenantid";
        public const string TenantIdShortClaim = "tid";
        public const string RolesShortClaim = "roles";

        /// <summary>Reads the caller from a validated principal. On failure
        /// <paramref name="failure"/> names the claim at fault, for logs; it never holds a claim
        /// value or a token.</summary>
        public static bool TryFrom(
            ClaimsPrincipal? user, [NotNullWhen(true)] out AdsIdentity? identity, out string failure)
        {
            identity = null;
            if (user is null)
            {
                failure = "no principal";
                return false;
            }

            if (!TryReadGuid(user, "object id", ObjectIdUriClaim, ObjectIdShortClaim, out var userId, out failure)
                || !TryReadGuid(user, "tenant id", TenantIdUriClaim, TenantIdShortClaim, out var tenantId, out failure))
                return false;

            var roles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var claim in user.Claims)
                if ((claim.Type == ClaimTypes.Role || claim.Type == RolesShortClaim) && claim.Value.Length > 0)
                    roles.Add(claim.Value);

            var personal = tenantId == ConsumerTenantId;
            identity = new AdsIdentity(userId, tenantId, personal ? userId : tenantId, personal, roles);
            failure = "";
            return true;
        }

        private static bool TryReadGuid(
            ClaimsPrincipal user, string label, string uriClaim, string shortClaim,
            out Guid value, out string failure)
        {
            value = Guid.Empty;
            var fromUri = TryParseClaim(user, uriClaim, out var uriValue, out var uriPresent);
            var fromShort = TryParseClaim(user, shortClaim, out var shortValue, out var shortPresent);

            if ((uriPresent && !fromUri) || (shortPresent && !fromShort))
            {
                failure = $"the {label} claim is not a usable GUID";
                return false;
            }

            if (!uriPresent && !shortPresent)
            {
                failure = $"the {label} claim is missing";
                return false;
            }

            if (uriPresent && shortPresent && uriValue != shortValue)
            {
                failure = $"the two spellings of the {label} claim disagree";
                return false;
            }

            value = uriPresent ? uriValue : shortValue;
            failure = "";
            return true;
        }

        /// <summary>Reads every claim of one type: all must parse to one non-empty GUID.</summary>
        private static bool TryParseClaim(ClaimsPrincipal user, string type, out Guid value, out bool present)
        {
            value = Guid.Empty;
            present = false;
            var ok = true;
            foreach (var claim in user.FindAll(type))
            {
                if (!present)
                {
                    present = true;
                    ok = Guid.TryParse(claim.Value, out value) && value != Guid.Empty;
                }
                else if (!Guid.TryParse(claim.Value, out var other) || other != value)
                {
                    ok = false;
                }
            }

            return ok;
        }

        /// <summary>Whether stored text (an approval records the claim as written) names this
        /// user. Compared as GUIDs: casing, braces and hyphens must not decide identity.</summary>
        public static bool SameUser(string? stored, Guid userId) =>
            Guid.TryParse(stored, out var parsed) && parsed != Guid.Empty && parsed == userId;
    }
}
