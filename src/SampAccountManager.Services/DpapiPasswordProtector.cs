using System.Security.Cryptography;
using System.Text;
using SampAccountManager.Core;

namespace SampAccountManager.Services;

/// <summary>Protejeaza parolele cu DPAPI (legat de utilizatorul Windows curent).</summary>
public sealed class DpapiPasswordProtector : IPasswordProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SampAccountManager.v1");

    public string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(data);
    }
}
