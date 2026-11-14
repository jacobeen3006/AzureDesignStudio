using System.Globalization;
using System.Text;

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

        /// <summary><c>name &lt;userId&gt;</c>, or just the id without a name. The name is the token's
        /// <c>name</c> claim, which the account holder controls, so it is sanitized first
        /// (<see cref="SanitizeDisplayName"/>): no control or format characters, no angle brackets
        /// (the display text can never forge the <c>&lt;guid&gt;</c> suffix), collapsed whitespace, and
        /// cut on a text-element boundary so the text never exceeds <see cref="PrincipalMaxLength"/>.</summary>
        public string Principal
        {
            get
            {
                var suffix = $" <{UserIdText}>";
                var name = SanitizeDisplayName(DisplayName, PrincipalMaxLength - suffix.Length);
                return name.Length == 0 ? UserIdText : name + suffix;
            }
        }

        /// <summary>Makes a token display name safe for audit rows, approvals and log lines (WR-03).
        /// Drops control, format (bidi overrides, zero-width), separator-line, private-use, unassigned and
        /// unpaired-surrogate characters, turns <c>&lt;</c> and <c>&gt;</c> into spaces, collapses every
        /// whitespace run to one space, trims, and cuts at <paramref name="maxLength"/> UTF-16 units on a
        /// text-element boundary. Returns an empty string when nothing is left.</summary>
        public static string SanitizeDisplayName(string? raw, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(raw) || maxLength <= 0)
                return string.Empty;

            var cleaned = new StringBuilder(Math.Min(raw.Length, 512));
            var pendingSpace = false;
            foreach (var rune in raw.EnumerateRunes())
            {
                var category = Rune.GetUnicodeCategory(rune);
                if (rune.Value == 0xFFFD // what EnumerateRunes yields for an unpaired surrogate
                    || category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
                        or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
                        or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                {
                    // Tabs, CR and LF are controls that act as separators; the rest vanish.
                    if (rune.Value is '\t' or '\r' or '\n')
                        pendingSpace = cleaned.Length > 0;
                    continue;
                }

                if (rune.Value is '<' or '>' || Rune.IsWhiteSpace(rune))
                {
                    pendingSpace = cleaned.Length > 0;
                    continue;
                }

                if (pendingSpace)
                    cleaned.Append(' ');
                pendingSpace = false;
                cleaned.Append(rune.ToString());
            }

            var text = cleaned.ToString();
            if (text.Length <= maxLength)
                return text;

            var cut = new StringBuilder(maxLength);
            var elements = StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext())
            {
                var element = (string)elements.Current;
                if (cut.Length + element.Length > maxLength)
                    break;
                cut.Append(element);
            }
            return cut.ToString().TrimEnd();
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
