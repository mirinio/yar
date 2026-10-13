using System.Net.Sockets;
using Yar.Core;

namespace Yar.Backends.Snap;

/// <summary>Installed snaps, read from the snapd daemon over its local socket.</summary>
public sealed class SnapProvider : ISoftwareProvider, IUpdateProvider, IDisposable
{
    public const string ProviderId = "snap";
    public const string StoreSourceId = "snap-store";
    public const string DefaultSocketPath = "/run/snapd.socket";

    private const int MaxResponseBytes = 32 * 1024 * 1024;

    private readonly string _socketPath;
    private readonly HttpClient _client;

    public SnapProvider(string socketPath = DefaultSocketPath)
        : this(socketPath, new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        })
    {
    }

    /// <summary>For tests: talk to snapd through the given handler instead of the socket.</summary>
    public SnapProvider(string socketPath, HttpMessageHandler handler)
    {
        _socketPath = socketPath;
        _client = new HttpClient(handler)
        {
            // The host name is ignored; requests go to the socket.
            BaseAddress = new Uri("http://localhost/"),
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
    }

    public string Id => ProviderId;

    public ProviderFacts Facts => new(ProviderId, "https://snapcraft.io/",
    [
        new("Installed snaps", "/snap"),
        new("Snap data", "/var/lib/snapd"),
    ]);

    public async Task<UpdateCheck> GetUpdatesAsync(IReadOnlyList<AppInfo> installed, CancellationToken ct)
    {
        using var response = await _client.GetAsync("v2/find?select=refresh", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return new UpdateCheck(SnapdJson.ParseRefreshable(json).Select(snap =>
        {
            var app = installed.FirstOrDefault(a => a.Id == snap.Name);
            return new UpdateInfo(
                ProviderId, snap.Name, app?.Name ?? snap.Title ?? snap.Name, app?.Version, snap.Version,
                UpdateSeverity.Normal, StoreSourceId, app?.Id);
        }).ToList());
    }

    public Task<ProviderStatus> ProbeAsync(CancellationToken ct) =>
        Task.FromResult(File.Exists(_socketPath) ? ProviderStatus.Available : ProviderStatus.NotInstalled);

    public Task<IReadOnlyList<SourceInfo>> GetSourcesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SourceInfo>>(
        [
            new SourceInfo(
                ProviderId, StoreSourceId, "Snap Store", Description: null, "https://snapcraft.io/store",
                Enabled: true, SourceScope.System, TrustLevel.Unknown, new Dictionary<string, string>()),
        ]);

    public async Task<IReadOnlyList<AppInfo>> ClaimAppsAsync(DesktopEntryIndex unclaimed, CancellationToken ct)
    {
        using var response = await _client.GetAsync("v2/snaps", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return SnapdJson.ParseInstalledApps(json)
            .Select(snap => ToApp(snap, LaunchersOf(snap, unclaimed)))
            .ToList();
    }

    public void Dispose() => _client.Dispose();

    private static List<DesktopEntry> LaunchersOf(SnapPackage snap, DesktopEntryIndex unclaimed) =>
        unclaimed.Entries
            .Where(entry => snap.DesktopFiles.Contains(entry.Path) ||
                entry.GetKey("X-SnapInstanceName") == snap.Name)
            .OrderByDescending(entry => entry.DesktopId == $"{snap.Name}_{snap.Name}.desktop")
            .ToList();

    private static AppInfo ToApp(SnapPackage snap, List<DesktopEntry> launchers)
    {
        var primary = launchers.FirstOrDefault();
        var technical = new Dictionary<string, string> { ["snap"] = snap.Name };
        Add("revision", snap.Revision);
        Add("channel", snap.Channel);
        Add("publisher", snap.Publisher);
        Add("confinement", snap.Confinement);
        // snapd calls this "install-date", but it changes with every update.
        Add("refreshed", snap.Refreshed);

        var iconIsPath = primary?.Icon is not null && Path.IsPathFullyQualified(primary.Icon);
        return new AppInfo(
            ProviderId,
            Id: snap.Name,
            Name: snap.Title ?? primary?.Name ?? snap.Name,
            Summary: snap.Summary ?? primary?.Comment,
            IconName: iconIsPath ? null : primary?.Icon,
            IconPath: iconIsPath ? primary!.Icon : null,
            Version: snap.Version,
            SourceId: StoreSourceId,
            SizeBytes: snap.InstalledSizeBytes,
            InstalledAt: null,
            LauncherPaths: launchers.Select(l => l.Path).ToList(),
            ComponentId: null,
            Technical: technical)
        {
            InstallPath = new[] { $"/snap/{snap.Name}/current", $"/var/lib/snapd/snap/{snap.Name}/current" }
                .FirstOrDefault(Directory.Exists),
        };

        void Add(string key, string? value)
        {
            if (value is not null)
                technical[key] = value;
        }
    }
}
