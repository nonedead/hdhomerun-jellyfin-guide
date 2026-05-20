using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.HDHomeRunGuide.Configuration;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HDHomeRunGuide.Services;

/// <summary>
/// Coordinates guide refreshes and stores refresh status in plugin configuration.
/// </summary>
public sealed class HDHomeRunGuideService
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly HDHomeRunClient _client;
    private readonly GuideWriter _writer;
    private readonly LiveTvConfigurator _liveTvConfigurator;
    private readonly ILogger<HDHomeRunGuideService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HDHomeRunGuideService"/> class.
    /// </summary>
    /// <param name="client">HDHomeRun client.</param>
    /// <param name="writer">Guide writer.</param>
    /// <param name="liveTvConfigurator">Live TV configurator.</param>
    /// <param name="logger">Logger.</param>
    public HDHomeRunGuideService(
        HDHomeRunClient client,
        GuideWriter writer,
        LiveTvConfigurator liveTvConfigurator,
        ILogger<HDHomeRunGuideService> logger)
    {
        _client = client;
        _writer = writer;
        _liveTvConfigurator = liveTvConfigurator;
        _logger = logger;
    }

    /// <summary>
    /// Refreshes guide and lineup files.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Refresh result.</returns>
    public async Task<RefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<RefreshResult> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is not available.");
        var config = plugin.Configuration;

        if (string.IsNullOrWhiteSpace(config.TunerAddress))
        {
            throw new InvalidOperationException("Configure a tuner IP address before refreshing guide data.");
        }

        try
        {
            var discover = await _client.GetDiscoverInfoAsync(config.TunerAddress, cancellationToken).ConfigureAwait(false);
            var lineup = await _client.GetLineupAsync(discover, cancellationToken).ConfigureAwait(false);
            var result = config.UseXmlTvGuideData
                ? await _writer.WriteXmlTvAsync(
                    await _client.GetXmlTvAsync(discover.DeviceAuth, cancellationToken).ConfigureAwait(false),
                    lineup,
                    plugin.DataFolderPath,
                    config.SkipDisabledChannels,
                    cancellationToken).ConfigureAwait(false)
                : await _writer.WriteAsync(
                    await _client.GetGuideAsync(discover.DeviceAuth, cancellationToken).ConfigureAwait(false),
                    lineup,
                    plugin.DataFolderPath,
                    config.SkipDisabledChannels,
                    cancellationToken).ConfigureAwait(false);
            result = result with { TunerCount = discover.TunerCount };

            config.LastGuidePath = result.GuidePath;
            config.LastM3uPath = result.M3uPath;
            config.LastRefreshUtc = DateTimeOffset.UtcNow.ToString("O");
            config.LastTunerCount = discover.TunerCount;
            config.LastError = string.Empty;

            if (config.AutoConfigureLiveTv)
            {
                await _liveTvConfigurator.ConfigureAsync(result, discover, config, cancellationToken).ConfigureAwait(false);
            }

            plugin.UpdateConfiguration(config);

            _logger.LogInformation(
                "HDHomeRun {GuideSource} refresh wrote {ChannelCount} channels and {ProgrammeCount} programmes to {GuidePath}",
                result.GuideSource,
                result.ChannelCount,
                result.ProgrammeCount,
                result.GuidePath);

            return result;
        }
        catch (Exception ex)
        {
            config.LastError = ex.Message;
            plugin.UpdateConfiguration(config);
            _logger.LogError(ex, "HDHomeRun guide refresh failed");
            throw;
        }
    }

    /// <summary>
    /// Scans a subnet for tuners.
    /// </summary>
    /// <param name="subnet">CIDR subnet.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Discovered tuners.</returns>
    public Task<IReadOnlyList<DiscoveredTuner>> DiscoverTunersAsync(string? subnet, CancellationToken cancellationToken)
    {
        return DiscoverTunersCoreAsync(subnet, cancellationToken);
    }

    /// <summary>
    /// Finds tuners with Jellyfin's built-in HDHomeRun discovery, stores the first physical device, and refreshes the guide.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Refresh result.</returns>
    public async Task<RefreshResult> AddMyTunersAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is not available.");
        var config = plugin.Configuration;
        var inferredSubnets = GetScanSubnets(config.ScanSubnet);
        var tuners = await DiscoverTunersCoreAsync(config.ScanSubnet, cancellationToken).ConfigureAwait(false);
        var tuner = tuners.FirstOrDefault();

        if (tuner is null)
        {
            throw new InvalidOperationException("Jellyfin did not find an HDHomeRun tuner.");
        }

        config.TunerAddress = tuner.Address;
        if (string.IsNullOrWhiteSpace(config.ScanSubnet) && inferredSubnets.Count == 1)
        {
            config.ScanSubnet = inferredSubnets[0];
        }

        config.AutoConfigureLiveTv = true;
        plugin.UpdateConfiguration(config);

        _logger.LogInformation(
            "Selected HDHomeRun {DeviceId} at {Address} from Jellyfin discovery",
            tuner.DeviceId,
            tuner.Address);

        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tests the configured tuner address.
    /// </summary>
    /// <param name="address">Tuner address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Discovered tuner.</returns>
    public async Task<DiscoveredTuner> TestTunerAsync(string address, CancellationToken cancellationToken)
    {
        var discover = await _client.GetDiscoverInfoAsync(address, cancellationToken).ConfigureAwait(false);
        return new DiscoveredTuner(
            HDHomeRunClient.NormalizeBaseUri(address).Host,
            discover.DeviceId,
            string.IsNullOrWhiteSpace(discover.FriendlyName) ? "HDHomeRun" : discover.FriendlyName,
            discover.TunerCount);
    }

    private async Task<IReadOnlyList<DiscoveredTuner>> DiscoverTunersCoreAsync(string? subnet, CancellationToken cancellationToken)
    {
        var builtInTuners = await DiscoverBuiltInTunersAsync(cancellationToken).ConfigureAwait(false);
        if (builtInTuners.Count > 0)
        {
            return builtInTuners;
        }

        var results = new List<DiscoveredTuner>();
        foreach (var scanSubnet in GetScanSubnets(subnet))
        {
            try
            {
                results.AddRange(await _client.DiscoverTunersAsync(scanSubnet, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Could not scan HDHomeRun subnet {Subnet}", scanSubnet);
            }
        }

        return results
            .GroupBy(item => item.Address, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Address, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<DiscoveredTuner>> DiscoverBuiltInTunersAsync(CancellationToken cancellationToken)
    {
        var tunerHosts = await _liveTvConfigurator.DiscoverHdhomerunTunersAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<DiscoveredTuner>();
        var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tunerHost in tunerHosts)
        {
            var address = ReadAddress(tunerHost);
            if (string.IsNullOrWhiteSpace(address) || !seenAddresses.Add(address))
            {
                continue;
            }

            results.Add(await EnrichBuiltInTunerAsync(tunerHost, address, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<DiscoveredTuner> EnrichBuiltInTunerAsync(
        TunerHostInfo tunerHost,
        string address,
        CancellationToken cancellationToken)
    {
        try
        {
            var discover = await _client.GetDiscoverInfoAsync(address, cancellationToken).ConfigureAwait(false);
            return new DiscoveredTuner(
                address,
                FirstNotEmpty(discover.DeviceId, tunerHost.DeviceId),
                FirstNotEmpty(discover.FriendlyName, tunerHost.FriendlyName, "HDHomeRun"),
                discover.TunerCount > 0 ? discover.TunerCount : Math.Max(tunerHost.TunerCount, 1));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enrich Jellyfin-discovered HDHomeRun at {Address}", address);
            return new DiscoveredTuner(
                address,
                tunerHost.DeviceId,
                FirstNotEmpty(tunerHost.FriendlyName, "HDHomeRun"),
                Math.Max(tunerHost.TunerCount, 1));
        }
    }

    private static string ReadAddress(TunerHostInfo tunerHost)
    {
        if (Uri.TryCreate(tunerHost.Url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        if (Uri.TryCreate("http://" + tunerHost.Url, UriKind.Absolute, out uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        return string.Empty;
    }

    private static string FirstNotEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> GetScanSubnets(string? configuredSubnet)
    {
        if (!string.IsNullOrWhiteSpace(configuredSubnet))
        {
            return [configuredSubnet];
        }

        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(address => address.Address)
            .Where(IsPrivateAddress)
            .Select(ToLocal24Subnet)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    private static string ToLocal24Subnet(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
    }
}
