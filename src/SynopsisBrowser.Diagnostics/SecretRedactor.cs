using System.Text.RegularExpressions;
using SynopsisBrowser.Core;

namespace SynopsisBrowser.Diagnostics;

public sealed partial class SecretRedactor : ISecretRedactor
{
    private static readonly string[] SensitiveNames =
    [
        "authorization", "cookie", "set-cookie", "password", "passwd", "secret", "token", "api_key", "apikey",
        "access_token", "refresh_token", "client_secret", "private_key", "app_key"
    ];

    public string Redact(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;

        var output = input;
        foreach (var name in SensitiveNames)
        {
            output = Regex.Replace(output,
                $"(?im)([\\\"']?{Regex.Escape(name)}[\\\"']?\\s*[:=]\\s*[\\\"']?)([^\\\"'\\s,;}}]+)",
                "$1[REDACTED]",
                RegexOptions.IgnoreCase);
        }

        output = BearerRegex().Replace(output, "$1[REDACTED]");
        return output;
    }

    [GeneratedRegex("(?i)(Bearer\\s+)[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerRegex();
}
