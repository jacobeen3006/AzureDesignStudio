using System;
using Xunit;

namespace AzureDesignStudio.Core.Tests.Support
{
    /// <summary>
    /// A fact that needs a directory the current user really cannot write to. xUnit 2 has no runtime skip API,
    /// so the attribute decides at discovery time: Windows sets directory permissions through ACLs (not done here)
    /// and root ignores a read-only directory.
    /// </summary>
    public sealed class UnixNonRootFactAttribute : FactAttribute
    {
        public UnixNonRootFactAttribute()
        {
            if (OperatingSystem.IsWindows())
                Skip = "Needs Unix file modes; directory ACL setup differs on Windows.";
            else if (Environment.IsPrivilegedProcess)
                Skip = "Running as root: a read-only directory does not restrict root.";
        }
    }
}
