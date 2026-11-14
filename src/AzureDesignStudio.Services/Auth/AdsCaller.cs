namespace AzureDesignStudio.Services.Auth
{
    /// <summary>
    /// The caller of one call: the derived identity plus what the host says about itself. The
    /// single-user marker comes from the host through <c>UserState</c>, never from a claim: the
    /// in-process Desktop host is the one place it is set.
    /// </summary>
    public sealed record AdsCaller(AdsIdentity Identity, bool IsSingleUserHost, string? DisplayName)
    {
        /// <summary>Width of the audit columns that store <see cref="Principal"/>.</summary>
        public const int PrincipalMaxLength = 320;

        public Guid UserId => Identity.UserId;
        public Guid ScopeId => Identity.ScopeId;

        /// <summary>What stored rows record as the user from now on.</summary>
        public string UserIdText => UserId.ToString("D");

        /// <summary><c>name &lt;userId&gt;</c>, or just the id without a name. The name is
        /// trimmed so the text never exceeds <see cref="PrincipalMaxLength"/>.</summary>
        public string Principal
        {
            get
            {
                var name = DisplayName?.Trim();
                if (string.IsNullOrEmpty(name))
                    return UserIdText;
                var suffix = $" <{UserIdText}>";
                var room = PrincipalMaxLength - suffix.Length;
                if (name.Length > room)
                    name = name[..room].TrimEnd();
                return name.Length == 0 ? UserIdText : name + suffix;
            }
        }

        /// <summary>A single-user host and a personal-account scope are their own administrator.</summary>
        public bool CanAdministerGuardrails =>
            IsSingleUserHost || Identity.IsPersonalAccount || Identity.Roles.Contains(AdsIdentity.GuardrailAdminRole);

        public bool CanApprove =>
            IsSingleUserHost || Identity.IsPersonalAccount || Identity.Roles.Contains(AdsIdentity.DeployApproverRole);

        /// <summary>Self-approval follows the tenant's setting; only a single-user host always may.
        /// A personal account's implied approver role does not imply it.</summary>
        public bool MaySelfApprove(bool tenantAllowsSelfApproval) => IsSingleUserHost || tenantAllowsSelfApproval;
    }
}
