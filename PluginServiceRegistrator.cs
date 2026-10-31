using Jellyfin.Plugin.JellyShelf.Integration;
using Jellyfin.Plugin.JellyShelf.Reader;
using Jellyfin.Plugin.JellyShelf.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyShelf;

/// <summary>
/// Registers the plugin's services with the Jellyfin host container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // The embedded reader assets are read-only and shared by every request.
        serviceCollection.AddSingleton<ReaderAssets>();

        // Per-account reader state (reading preferences + last position per book).
        serviceCollection.AddSingleton<ReaderStateStore>();

        // jellyfin-web integration: Play/Read opens the reader (via File Transformation).
        serviceCollection.AddSingleton<FileTransformationBridge>();
        serviceCollection.AddHostedService<WebIntegrationService>();
    }
}
