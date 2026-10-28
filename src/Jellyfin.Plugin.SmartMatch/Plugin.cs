using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.SmartMatch.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SmartMatch;

/// <summary>
/// Smart Match plugin entry point. The constructor does no network, library or background work.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Stable plugin GUID. Never change it between releases.</summary>
    public const string PluginGuid = "4b7fecfd-bc55-4216-b032-2947ec6c22c9";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        if (ConfigurationValidator.Sanitize(Configuration))
        {
            SaveConfiguration();
        }
    }

    /// <summary>Gets the current instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Smart Match";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginGuid, CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override string Description => "Matches films and series with messy filenames. Never renames or moves files.";

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration typed)
        {
            var errors = ConfigurationValidator.Validate(typed);
            if (errors.Count > 0)
            {
                throw new ArgumentException("Invalid Smart Match configuration: " + string.Join(" ", errors), nameof(configuration));
            }
        }

        base.UpdateConfiguration(configuration);
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "SmartMatch",
                DisplayName = "Smart Match",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "auto_fix_high",
            },
            new PluginPageInfo
            {
                Name = "SmartMatchJs",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js",
            },
        };
    }
}
