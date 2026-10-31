using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyShelf.Configuration;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShelf.Integration;

/// <summary>
/// Keeps the jellyfin-web patches registered with File Transformation while
/// <see cref="PluginConfiguration.EnableWebIntegration"/> is on, and only then,
/// so the Play/Read button opens the JellyShelf reader.
/// </summary>
public sealed class WebIntegrationService : IHostedService
{
    private static readonly Guid _indexTransformationId = new("a1c4e9d2-7b36-4f81-9e22-5c8a0d3f6b14");
    private static readonly Guid _configTransformationId = new("c5f2a7b8-1d49-4e63-8a07-9b3e1f0c2d56");

    private readonly FileTransformationBridge _bridge;
    private readonly ILogger<WebIntegrationService> _logger;
    private readonly object _lock = new();
    private bool _registered;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebIntegrationService"/> class.
    /// </summary>
    /// <param name="bridge">The File Transformation bridge.</param>
    /// <param name="logger">The logger.</param>
    public WebIntegrationService(FileTransformationBridge bridge, ILogger<WebIntegrationService> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_bridge.IsAvailable)
        {
            _logger.LogInformation(
                "JellyShelf: File Transformation is not installed, so the Play-button integration is off. The reader still works at /JellyShelf/?id=<itemId>.");
        }

        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += OnConfigurationChanged;
            Apply(plugin.Configuration);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    private void Apply(PluginConfiguration configuration)
    {
        lock (_lock)
        {
            if (configuration.EnableWebIntegration == _registered)
            {
                return;
            }

            try
            {
                if (configuration.EnableWebIntegration)
                {
                    Register();
                }
                else
                {
                    _bridge.Remove(_indexTransformationId);
                    _bridge.Remove(_configTransformationId);
                    _registered = false;
                    _logger.LogInformation("JellyShelf: Play-button integration off.");
                }
            }
            catch (Exception exception)
            {
                // A File Transformation hiccup can only break the integration, never the reader.
                _logger.LogError(exception, "JellyShelf: updating the jellyfin-web integration failed.");
            }
        }
    }

    private void Register()
    {
        if (!_bridge.IsAvailable)
        {
            return;
        }

        if (_bridge.Register(_indexTransformationId, "^index\\.html$", nameof(TransformCallbacks.Index))
            && _bridge.Register(_configTransformationId, "^config\\.json$", nameof(TransformCallbacks.Config)))
        {
            _registered = true;
            _logger.LogInformation("JellyShelf: Play-button integration on; Play on a PDF/comic opens the JellyShelf reader.");
        }
        else
        {
            _logger.LogWarning("JellyShelf: File Transformation is loaded but did not accept the registration.");
        }
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration jellyShelf)
        {
            Apply(jellyShelf);
        }
    }
}
