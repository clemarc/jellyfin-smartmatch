using System;
using System.IO;
using Jellyfin.Plugin.SmartMatch.Jobs;
using Jellyfin.Plugin.SmartMatch.Library;
using Jellyfin.Plugin.SmartMatch.Matching;
using Jellyfin.Plugin.SmartMatch.Normalization;
using Jellyfin.Plugin.SmartMatch.Services;
using Jellyfin.Plugin.SmartMatch.Storage;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartMatch;

/// <summary>
/// Registers Smart Match services in Jellyfin's container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.TryAddSingleton(TimeProvider.System);
        serviceCollection.AddSingleton<IConfigProvider, PluginConfigProvider>();
        serviceCollection.AddSingleton(sp =>
        {
            // Under Jellyfin's plugin configuration directory, which survives plugin updates;
            // never inside a media mount.
            var paths = sp.GetRequiredService<IApplicationPaths>();
            return new JsonFileStore(Path.Combine(paths.PluginConfigurationsPath, "SmartMatch"), sp.GetRequiredService<ILogger<JsonFileStore>>());
        });
        serviceCollection.AddSingleton<SmartMatchStore>();
        serviceCollection.AddSingleton<IServerInfo, JellyfinServerInfo>();
        serviceCollection.AddSingleton<ILibraryGateway, JellyfinLibraryGateway>();
        serviceCollection.AddSingleton<IProviderSearchGateway, JellyfinProviderSearchGateway>();
        serviceCollection.AddSingleton<IItemEventSource, JellyfinItemEventSource>();
        serviceCollection.AddSingleton<IMediaNameNormalizer, MediaNameNormalizer>();
        serviceCollection.AddSingleton<ProviderSearchExecutor>();
        serviceCollection.AddSingleton<ProviderResultCache>();
        serviceCollection.AddSingleton<ItemLockRegistry>();
        serviceCollection.AddSingleton<SelfWriteTracker>();
        serviceCollection.AddSingleton<WorkerStatus>();
        serviceCollection.AddSingleton<ItemAnalyzer>();
        serviceCollection.AddSingleton<MatchApplicationService>();
        serviceCollection.AddSingleton<BulkApplyCoordinator>();
        serviceCollection.AddSingleton<AuditService>();
        serviceCollection.AddSingleton<ScanEventObserver>();
        serviceCollection.AddSingleton<SmartMatchWorker>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<SmartMatchWorker>());
    }
}
