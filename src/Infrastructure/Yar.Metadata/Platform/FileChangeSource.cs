using Yar.Core;

namespace Yar.Metadata;

/// <summary>Watches files in a set of directories and reports any change to them as one kind of change.</summary>
public sealed class FileChangeSource(
    IReadOnlyList<string> directories, string filter, bool includeSubdirectories, ChangeKinds kinds) : IChangeSource
{
    private readonly List<FileSystemWatcher> _watchers = [];

    public event Action<ChangeKinds>? Changed;

    /// <summary>Launchers appearing, changing or disappearing in any applications directory.</summary>
    public static FileChangeSource ForLaunchers(XdgDirs dirs) =>
        new(dirs.ApplicationDirs, "*.desktop", includeSubdirectories: true, ChangeKinds.Apps);

    /// <summary>Flatpak touches a file called <c>.changed</c> in an installation whenever anything in it changes.</summary>
    public static FileChangeSource ForFlatpak(IReadOnlyList<string> installationDirs) =>
        new(installationDirs, ".changed", includeSubdirectories: false, ChangeKinds.All);

    public void Start()
    {
        foreach (var directory in directories)
        {
            FileSystemWatcher? watcher = null;
            try
            {
                if (!Directory.Exists(directory))
                    continue;

                watcher = new FileSystemWatcher(directory, filter)
                {
                    IncludeSubdirectories = includeSubdirectories,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size |
                        NotifyFilters.Attributes | NotifyFilters.CreationTime,
                };
                watcher.Created += (_, _) => Changed?.Invoke(kinds);
                watcher.Changed += (_, _) => Changed?.Invoke(kinds);
                watcher.Deleted += (_, _) => Changed?.Invoke(kinds);
                watcher.Renamed += (_, _) => Changed?.Invoke(kinds);
                // Once the system stops delivering events, a watcher is of no use; the refresh button still works.
                var failed = watcher;
                watcher.Error += (_, _) => failed.EnableRaisingEvents = false;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // No more watches available, or the directory vanished: carry on without this one.
                watcher?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
    }
}
