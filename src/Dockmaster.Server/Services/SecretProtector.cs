using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Dockmaster.Server.Services;

/// <summary>Encrypts registry passwords and project secrets at rest with ASP.NET DataProtection (keys live in the data dir).</summary>
public sealed class SecretProtector
{
    private readonly IDataProtector _protector;
    public SecretProtector(IDataProtectionProvider provider) => _protector = provider.CreateProtector("dockmaster.secrets.v1");
    public string Protect(string plain) => string.IsNullOrEmpty(plain) ? "" : _protector.Protect(plain);

    /// <summary>
    /// Decrypts a stored value. Throws when it cannot: after the data-protection keys are lost a deploy with empty
    /// secrets or registry passwords would look fine and break the services.
    /// </summary>
    public string Unprotect(string protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return "";
        try { return _protector.Unprotect(protectedText); }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("a stored secret or password cannot be decrypted (were the keys in the data dir lost?) — enter it again", ex);
        }
    }
}
