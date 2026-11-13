using Azure.Identity;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using System.Text;
using AzureDesignStudio.Services;

namespace AzureDesignStudio.Server.Services
{
    /// <summary>
    /// Encrypts subscription secrets with one RSA key in Key Vault (RSA-OAEP).
    /// The vault is contacted on first use, not in the constructor: this is a singleton
    /// every Deploy RPC depends on, so a missing or placeholder vault name used to throw
    /// while the service was being constructed - before any RPC body could answer - and
    /// did so again on every call.
    /// </summary>
    public class CryptoService : ICryptoService
    {
        // RSA-OAEP with SHA-1 (the RsaOaep algorithm) spends 2 * 20 + 2 bytes of the modulus.
        private const int OaepOverheadBytes = 42;

        private readonly IConfiguration _configuration;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private (CryptographyClient Client, int MaxPlaintextBytes)? _crypto;

        public CryptoService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        private async Task<(CryptographyClient Client, int MaxPlaintextBytes)> CreateClientAsync()
        {
            var vaultName = _configuration["KeyVaultName"];
            var keyName = _configuration["KeyVaultKeyName"];
            if (!ServiceTools.IsConfigured(vaultName) || !ServiceTools.IsConfigured(keyName))
                throw new InvalidOperationException(
                    "Key Vault is not configured: set KeyVaultName and KeyVaultKeyName (user-secrets in development). " +
                    "Subscription secrets cannot be stored or read without it.");

            var credential = new DefaultAzureCredential();
            var keyClient = new KeyClient(new Uri($"https://{vaultName}.vault.azure.net/"), credential);
            var key = (await keyClient.GetKeyAsync(keyName).ConfigureAwait(false)).Value;

            var modulusBytes = key.Key.N?.Length ?? 0;
            var maxPlaintext = modulusBytes > OaepOverheadBytes ? modulusBytes - OaepOverheadBytes : int.MaxValue;
            return (new CryptographyClient(key.Id, credential), maxPlaintext);
        }


        private async Task<(CryptographyClient Client, int MaxPlaintextBytes)> GetCryptoAsync()
        {
            if (_crypto is { } ready)
                return ready;

            await _initLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Only a successful load is kept, so a vault that was briefly unreachable
                // is tried again on the next call instead of failing until restart.
                return _crypto ??= await CreateClientAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException("The Key Vault key could not be loaded.", ex);
            }
            finally
            {
                _initLock.Release();
            }
        }

        public async Task<string> Decrypt(string encryptedText)
        {
            var (client, _) = await GetCryptoAsync().ConfigureAwait(false);
            byte[] inputAsByteArray = Convert.FromBase64String(encryptedText);
            DecryptResult decryptResult = await client.DecryptAsync(EncryptionAlgorithm.RsaOaep, inputAsByteArray).ConfigureAwait(false);
            // Same encoding as Encrypt, spelled out rather than left to Encoding.Default.
            return Encoding.UTF8.GetString(decryptResult.Plaintext);
        }

        public async Task<string> Encrypt(string plainText)
        {
            var (client, maxPlaintextBytes) = await GetCryptoAsync().ConfigureAwait(false);
            byte[] inputAsByteArray = Encoding.UTF8.GetBytes(plainText);
            if (inputAsByteArray.Length > maxPlaintextBytes)
                throw new ArgumentException(
                    $"The secret is {inputAsByteArray.Length} bytes; the configured key can encrypt at most {maxPlaintextBytes}.",
                    nameof(plainText));

            EncryptResult encryptResult = await client.EncryptAsync(EncryptionAlgorithm.RsaOaep, inputAsByteArray).ConfigureAwait(false);
            return Convert.ToBase64String(encryptResult.Ciphertext);
        }
    }
}
