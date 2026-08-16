using System.IO;
using System.Text.Json;
using SynopsisBrowser.Core;

namespace SynopsisBrowser.App.Services;

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public JsonSettingsStore(string appDataDirectory)
    {
        Directory.CreateDirectory(appDataDirectory);
        _path = Path.Combine(appDataDirectory, "settings.json");
    }

    public BrowserSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new BrowserSettings();
            return JsonSerializer.Deserialize<BrowserSettings>(File.ReadAllText(_path)) ?? new BrowserSettings();
        }
        catch { return new BrowserSettings(); }
    }

    public void Save(BrowserSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, _json));
    }
}
