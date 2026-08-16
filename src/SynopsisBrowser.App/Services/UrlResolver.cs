namespace SynopsisBrowser.App.Services;

public sealed class UrlResolver
{
    public Uri Resolve(string input, string searchTemplate)
    {
        input = (input ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(input)) return new Uri("about:blank");

        if (Uri.TryCreate(input, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme is "http" or "https" or "file" or "about")) return absolute;

        if (input.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("127.0.0.1") || input.StartsWith("[::1]"))
            return new Uri("http://" + input);

        if (!input.Contains(' ') && input.Contains('.'))
        {
            if (Uri.TryCreate("https://" + input, UriKind.Absolute, out var hostUri)) return hostUri;
        }

        return new Uri(searchTemplate.Replace("{query}", Uri.EscapeDataString(input), StringComparison.Ordinal));
    }
}
