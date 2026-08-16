using System.IO;
using Microsoft.Web.WebView2.Core;

namespace SynopsisBrowser.App.Services;

public sealed class WebViewRuntimeService
{
    private readonly string _userDataFolder;
    private readonly Lazy<Task<CoreWebView2Environment>> _environment;

    public WebViewRuntimeService(string userDataFolder)
    {
        _userDataFolder = userDataFolder;
        Directory.CreateDirectory(_userDataFolder);
        _environment = new Lazy<Task<CoreWebView2Environment>>(() => CoreWebView2Environment.CreateAsync(userDataFolder: _userDataFolder));
    }

    public Task<CoreWebView2Environment> GetEnvironmentAsync() => _environment.Value;
}
