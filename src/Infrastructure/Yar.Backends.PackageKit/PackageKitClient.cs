using Tmds.DBus.Protocol;
using Yar.Backends.PackageKit.DBus;
using Yar.Core;

namespace Yar.Backends.PackageKit;

/// <summary>Talks to the PackageKit daemon on the system bus.</summary>
public sealed class PackageKitClient : IPackageKitClient, IDisposable
{
    private const string ServiceName = "org.freedesktop.PackageKit";
    private const string RootPath = "/org/freedesktop/PackageKit";

    // Bit positions from PackageKit's pk-enum.h: PK_FILTER_ENUM_NONE = 1, PK_FILTER_ENUM_INSTALLED = 2.
    private const ulong FilterNone = 1UL << 1;
    private const ulong FilterInstalled = 1UL << 2;

    // PK_ROLE_ENUM_REFRESH_CACHE in pk-enum.h.
    private const uint RoleRefreshCache = 13;

    // PK_EXIT_ENUM_SUCCESS in pk-enum.h.
    private const uint ExitSuccess = 1;

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private DBusConnection? _connection;
    private DBusService? _service;

    public async Task<PkBackend?> GetBackendAsync(CancellationToken ct)
    {
        var address = DBusAddress.System;
        if (address is null)
            return null;

        try
        {
            var service = await ConnectAsync(address, ct).ConfigureAwait(false);
            var root = service.CreatePackageKit(RootPath);
            var name = await root.GetBackendNameAsync().WaitAsync(ct).ConfigureAwait(false);
            var distro = await root.GetDistroIdAsync().WaitAsync(ct).ConfigureAwait(false);
            return new(name, distro);
        }
        catch (DBusErrorReplyException e) when (
            e.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner")
        {
            return null;
        }
        catch (DBusConnectionException)
        {
            // No system bus at all, as in a minimal container.
            return null;
        }
    }

    public async Task<IReadOnlyList<PkRepo>> GetRepoListAsync(CancellationToken ct)
    {
        var repos = new List<PkRepo>();
        await RunAsync(
            async transaction => [await transaction.WatchRepoDetailAsync(
                repo => { lock (repos) repos.Add(new(repo.RepoId, repo.Description, repo.Enabled)); },
                emitOnCapturedContext: false).ConfigureAwait(false)],
            transaction => transaction.GetRepoListAsync(FilterNone),
            ct).ConfigureAwait(false);
        return repos;
    }

    public Task SetRepoEnabledAsync(string repoId, bool enabled, CancellationToken ct) =>
        RunAsync(
            _ => Task.FromResult<IDisposable[]>([]),
            async transaction =>
            {
                // Lets the system show its password prompt instead of refusing outright.
                await transaction.SetHintsAsync(["interactive=true"]).ConfigureAwait(false);
                await transaction.RepoEnableAsync(repoId, enabled).ConfigureAwait(false);
            },
            ct);

    public Task<IReadOnlyList<PkPackage>> GetUpdatesAsync(CancellationToken ct) =>
        CollectPackagesAsync(transaction => transaction.GetUpdatesAsync(FilterNone), ct);

    public async Task<TimeSpan?> GetTimeSinceRefreshAsync(CancellationToken ct)
    {
        var service = await ConnectAsync(DBusAddress.System, ct).ConfigureAwait(false);
        var seconds = await service.CreatePackageKit(RootPath)
            .GetTimeSinceActionAsync(RoleRefreshCache).WaitAsync(ct).ConfigureAwait(false);
        // The daemon answers with the largest number when it has never refreshed.
        return seconds == uint.MaxValue ? null : TimeSpan.FromSeconds(seconds);
    }

    public Task<IReadOnlyList<PkPackage>> ResolveInstalledAsync(IReadOnlyList<string> names, CancellationToken ct) =>
        names.Count == 0
            ? Task.FromResult<IReadOnlyList<PkPackage>>([])
            : CollectPackagesAsync(transaction => transaction.ResolveAsync(FilterInstalled, [.. names]), ct);

    public Task<IReadOnlyList<PkPackage>> SearchInstalledFileAsync(string path, CancellationToken ct) =>
        CollectPackagesAsync(transaction => transaction.SearchFilesAsync(FilterInstalled, [path]), ct);

    public async Task<IReadOnlyDictionary<string, long>> GetSizesAsync(
        IReadOnlyList<string> packageIds, CancellationToken ct)
    {
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        if (packageIds.Count == 0)
            return sizes;

        await RunAsync(
            async transaction => [await transaction.WatchDetailsAsync(
                details =>
                {
                    if (details.TryGetValue("package-id", out var id) && details.TryGetValue("size", out var size) &&
                        id.Type == VariantValueType.String && size.Type == VariantValueType.UInt64)
                    {
                        lock (sizes) sizes[id.GetString()] = (long)Math.Min(size.GetUInt64(), long.MaxValue);
                    }
                },
                emitOnCapturedContext: false).ConfigureAwait(false)],
            transaction => transaction.GetDetailsAsync([.. packageIds]),
            ct).ConfigureAwait(false);
        return sizes;
    }

    /// <summary>Calls <paramref name="onChange"/> whenever the daemon announces a change. Dispose the result to stop.</summary>
    public async Task<IDisposable> WatchChangesAsync(Action<ChangeKinds> onChange, CancellationToken ct)
    {
        var service = await ConnectAsync(DBusAddress.System, ct).ConfigureAwait(false);
        var root = service.CreatePackageKit(RootPath);
        Action installed = () => onChange(ChangeKinds.Apps | ChangeKinds.Updates);
        Action repos = () => onChange(ChangeKinds.Sources | ChangeKinds.Updates);
        Action updates = () => onChange(ChangeKinds.Updates);

        var subscriptions = new List<IDisposable>();
        try
        {
            subscriptions.Add(await root.WatchInstalledChangedAsync(installed, emitOnCapturedContext: false).ConfigureAwait(false));
            subscriptions.Add(await root.WatchRepoListChangedAsync(repos, emitOnCapturedContext: false).ConfigureAwait(false));
            subscriptions.Add(await root.WatchUpdatesChangedAsync(updates, emitOnCapturedContext: false).ConfigureAwait(false));
        }
        catch
        {
            foreach (var subscription in subscriptions)
                subscription.Dispose();
            throw;
        }
        return new Subscriptions(subscriptions);
    }

    private sealed class Subscriptions(List<IDisposable> items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
                item.Dispose();
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connectLock.Dispose();
    }

    private async Task<IReadOnlyList<PkPackage>> CollectPackagesAsync(
        Func<Transaction, Task> start, CancellationToken ct)
    {
        var packages = new List<PkPackage>();
        await RunAsync(
            async transaction => [await transaction.WatchPackageAsync(
                package =>
                {
                    if (PkPackage.Parse(package.PackageId, package.Summary, package.Info) is { } parsed)
                        lock (packages) packages.Add(parsed);
                },
                emitOnCapturedContext: false).ConfigureAwait(false)],
            start,
            ct).ConfigureAwait(false);
        return packages;
    }

    /// <summary>
    /// Runs one PackageKit transaction: subscribe to its result signals, start it, wait until it finishes.
    /// </summary>
    private async Task RunAsync(
        Func<Transaction, Task<IDisposable[]>> subscribe, Func<Transaction, Task> start, CancellationToken ct)
    {
        var service = await ConnectAsync(DBusAddress.System, ct).ConfigureAwait(false);
        var path = await service.CreatePackageKit(RootPath).CreateTransactionAsync().WaitAsync(ct).ConfigureAwait(false);
        var transaction = service.CreateTransaction(path);

        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PackageKitException? error = null;
        var subscriptions = new List<IDisposable>();
        try
        {
            subscriptions.Add(await transaction.WatchErrorCodeAsync(
                e => error = new PackageKitException(e.Code, e.Details), emitOnCapturedContext: false).ConfigureAwait(false));
            subscriptions.Add(await transaction.WatchFinishedAsync(
                result =>
                {
                    if (result.Exit == ExitSuccess)
                        finished.TrySetResult();
                    else
                        finished.TrySetException(error ?? new PackageKitException(result.Exit, "The request did not finish."));
                },
                emitOnCapturedContext: false).ConfigureAwait(false));
            subscriptions.AddRange(await subscribe(transaction).ConfigureAwait(false));

            await start(transaction).WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await finished.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Tell the daemon to stop working for us; it does not matter if that fails.
                _ = transaction.CancelAsync().ContinueWith(
                    static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                throw;
            }
        }
        finally
        {
            foreach (var subscription in subscriptions)
                subscription.Dispose();
        }
    }

    private async Task<DBusService> ConnectAsync(string? address, CancellationToken ct)
    {
        if (address is null)
            throw new InvalidOperationException("There is no system message bus on this computer.");

        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_service is { } existing)
                return existing;

            var connection = new DBusConnection(address);
            try
            {
                await connection.ConnectAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
            _connection = connection;
            var service = new DBusService(connection, ServiceName);
            _service = service;
            return service;
        }
        finally
        {
            _connectLock.Release();
        }
    }
}
