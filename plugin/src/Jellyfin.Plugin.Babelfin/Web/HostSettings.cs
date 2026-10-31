using System;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Babelfin.Web;

/// <summary>
/// The server settings Jellyfin itself uses when it wires up /web (read once at startup, as Jellyfin does):
/// the base URL prefix and whether plain-HTTP requests get redirected to HTTPS.
/// Read by reflection from the application host so a future Jellyfin that renames these
/// makes Babelfin step aside instead of failing to load.
/// </summary>
public sealed class HostSettings
{
    /// <summary>Gets the base URL ("" or "/something", no trailing slash).</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether Jellyfin redirects plain HTTP to HTTPS.</summary>
    public bool RedirectsToHttps { get; init; }

    /// <summary>Gets a value indicating whether the settings could be read. If not, Babelfin leaves the web client alone.</summary>
    public bool Resolved { get; init; }

    /// <summary>
    /// Reads the settings from Jellyfin's application host.
    /// </summary>
    /// <param name="applicationHost">The IServerApplicationHost passed to the plugin.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>The settings.</returns>
    public static HostSettings Read(object? applicationHost, ILogger? logger = null)
    {
        try
        {
            if (applicationHost is null)
            {
                throw new InvalidOperationException("no application host");
            }

            var hostType = applicationHost.GetType();
            var configManager = hostType.GetProperty("ConfigurationManager")?.GetValue(applicationHost)
                ?? throw new InvalidOperationException("ConfigurationManager not found");
            var getConfiguration = configManager.GetType().GetMethod("GetConfiguration", new[] { typeof(string) })
                ?? throw new InvalidOperationException("GetConfiguration(string) not found");
            var network = getConfiguration.Invoke(configManager, new object[] { "network" })
                ?? throw new InvalidOperationException("network configuration missing");
            var networkType = network.GetType();
            var baseUrl = networkType.GetProperty("BaseUrl")?.GetValue(network) as string ?? string.Empty;
            var requireHttps = networkType.GetProperty("RequireHttps")?.GetValue(network) as bool? ?? false;
            var listenWithHttps = hostType.GetProperty("ListenWithHttps")?.GetValue(applicationHost) as bool? ?? false;

            return new HostSettings
            {
                BaseUrl = Normalize(baseUrl),
                RedirectsToHttps = requireHttps && listenWithHttps,
                Resolved = true,
            };
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[Babelfin] Couldn't read the server's base URL settings; Babelfin will leave the web client unchanged");
            return new HostSettings { Resolved = false };
        }
    }

    /// <summary>
    /// Normalizes a base URL the same way Jellyfin's NetworkConfiguration does.
    /// </summary>
    /// <param name="value">Raw value.</param>
    /// <returns>"" or "/prefix".</returns>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (value[0] != '/')
        {
            value = "/" + value;
        }

        return value.TrimEnd('/');
    }
}
