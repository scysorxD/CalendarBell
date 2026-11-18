using System.Security.Cryptography;
using System.Text;
using CalendarBell.Core.Interfaces;

namespace CalendarBell.Infrastructure.Security;

/// <summary>The stored URL could not be decrypted (different Windows user/profile, or tampered data).</summary>
public sealed class UrlProtectionException : Exception
{
    public UrlProtectionException()
    {
    }

    public UrlProtectionException(string message)
        : base(message)
    {
    }

    public UrlProtectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Encrypts published calendar URLs with DPAPI bound to the current Windows user (spec §5.4).
/// A published ICS link is effectively a password: it is never stored or logged in clear text.
/// </summary>
public sealed class DpapiUrlProtector : IUrlProtector
{
    // Fixed application entropy: another app running as the same user cannot decrypt the blob by accident.
    private static readonly byte[] Entropy = "CalendarBell.CalendarUrl.v1"u8.ToArray();

    public string Protect(string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        var plain = Encoding.UTF8.GetBytes(url);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public string Unprotect(string protectedUrl)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedUrl);
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(protectedUrl), Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new UrlProtectionException("The stored calendar URL could not be decrypted.", ex);
        }
    }
}
