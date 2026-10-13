namespace AzureDesignStudio.Services
{
    /// <summary>
    /// Host-provided sign-in/sign-out overrides. The WASM host leaves them null
    /// and the drawers use the server redirect flow (NavigateToLogin/Logout);
    /// the MAUI desktop host wires them to its native MSAL flow.
    /// Same host-override convention as <see cref="AdsContext.StencilLoader"/>.
    /// </summary>
    public class AdsHostAuth
    {
        public Func<Task>? SignIn { get; set; }
        public Func<Task>? SignOut { get; set; }
    }
}
