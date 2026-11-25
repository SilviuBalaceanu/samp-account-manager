namespace SampAccountManager.Core;

public interface IPasswordProtector
{
    string Protect(string plain);
    string Unprotect(string protectedValue);
}
