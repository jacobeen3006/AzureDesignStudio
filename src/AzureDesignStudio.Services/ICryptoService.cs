namespace AzureDesignStudio.Services
{
    /// <summary>
    /// Protects the client secret of a linked service principal. What comes back from
    /// <see cref="Encrypt"/> is what is stored; only <see cref="Decrypt"/> turns it back into
    /// the secret. The server encrypts with a Key Vault key; the desktop app keeps the secret
    /// in the operating system's credential store and stores only a reference to it.
    /// </summary>
    public interface ICryptoService
    {
        Task<string> Encrypt(string plainText);
        Task<string> Decrypt(string encryptedText);
    }
}