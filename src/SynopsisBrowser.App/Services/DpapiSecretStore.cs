using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SynopsisBrowser.Core;

namespace SynopsisBrowser.App.Services;

/// <summary>
/// Stores secrets encrypted with Windows DPAPI for the current Windows user.
/// The settings file never contains the plaintext API key.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SynopsisBrowser.AI.Secrets.v1");
    private readonly string _path;
    private readonly object _gate = new();

    public DpapiSecretStore(string appDataDirectory)
    {
        Directory.CreateDirectory(appDataDirectory);
        _path = Path.Combine(appDataDirectory, "secrets.json");
    }

    public string? GetSecret(string name)
    {
        lock (_gate)
        {
            try
            {
                var values = ReadAll();
                if (!values.TryGetValue(name, out var encoded) || string.IsNullOrWhiteSpace(encoded)) return null;
                var encrypted = Convert.FromBase64String(encoded);
                var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                return null;
            }
        }
    }

    public void SetSecret(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            DeleteSecret(name);
            return;
        }

        lock (_gate)
        {
            var values = ReadAll();
            var plain = Encoding.UTF8.GetBytes(value);
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            values[name] = Convert.ToBase64String(encrypted);
            WriteAll(values);
        }
    }

    public void DeleteSecret(string name)
    {
        lock (_gate)
        {
            var values = ReadAll();
            if (!values.Remove(name)) return;
            WriteAll(values);
        }
    }

    private Dictionary<string, string> ReadAll()
    {
        if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path))
                   ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void WriteAll(Dictionary<string, string> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
    }
}
