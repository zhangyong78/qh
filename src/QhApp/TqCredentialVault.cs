using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;

namespace QhApp;

internal sealed record TqStoredCredential(string Account, string Password);

internal sealed class TqCredentialVault
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QhResearch/TqSdk/CurrentUser/v1");
    private readonly string _path;

    public TqCredentialVault()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QhResearch");
        _path = Path.Combine(root, "tqsdk-credential.dpapi");
    }

    public bool Exists => File.Exists(_path);

    public void Save(string account, string password)
    {
        if (string.IsNullOrWhiteSpace(account)) throw new ArgumentException("账号不能为空", nameof(account));
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("密码不能为空", nameof(password));
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new TqStoredCredential(account.Trim(), password));
        var protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(_path, protectedBytes);
        CryptographicOperations.ZeroMemory(plaintext);
    }

    public TqStoredCredential? Load()
    {
        if (!File.Exists(_path)) return null;
        var protectedBytes = File.ReadAllBytes(_path);
        var plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<TqStoredCredential>(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); CryptographicOperations.ZeroMemory(protectedBytes); }
    }

    public void Delete()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
