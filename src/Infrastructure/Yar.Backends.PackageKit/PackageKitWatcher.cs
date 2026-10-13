using Tmds.DBus.Protocol;
using Yar.Core;

namespace Yar.Backends.PackageKit;

/// <summary>Listens to the signals PackageKit sends when packages, repositories or pending updates change.</summary>
public sealed class PackageKitWatcher(PackageKitClient client) : IChangeSource
{
    private readonly CancellationTokenSource _stopping = new();
    private IDisposable? _subscription;

    public event Action<ChangeKinds>? Changed;

    public void Start() => _ = SubscribeAsync();

    public void Dispose()
    {
        _stopping.Cancel();
        _subscription?.Dispose();
        _stopping.Dispose();
    }

    private async Task SubscribeAsync()
    {
        try
        {
            _subscription = await client.WatchChangesAsync(kinds => Changed?.Invoke(kinds), _stopping.Token)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is DBusExceptionBase or InvalidOperationException or OperationCanceledException or ObjectDisposedException)
        {
            // No PackageKit here: there is nothing to listen to, and the refresh button still works.
        }
    }
}
