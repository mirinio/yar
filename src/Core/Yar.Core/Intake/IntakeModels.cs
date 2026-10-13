namespace Yar.Core;

/// <summary>What a file the user brought turned out to be.</summary>
public enum BundleKind
{
    /// <summary>One file that is the whole app.</summary>
    AppImage,

    /// <summary>One program file with nothing around it.</summary>
    Program,

    /// <summary>A zip or tar archive holding a ready-to-run app.</summary>
    Archive,

    /// <summary>A package for this distribution's package manager (.rpm or .deb).</summary>
    Package,

    /// <summary>A Flatpak app as a file (.flatpak) or a pointer to one (.flatpakref).</summary>
    FlatpakFile,

    /// <summary>Something Yar does not install. The report says what it is and why.</summary>
    Unsupported,
}

public enum CheckState { Good, Warning, Bad }

/// <summary>One line of the "can this run here?" checklist.</summary>
public sealed record IntakeCheck(CheckState State, string Text);

/// <summary>A program inside an archive that could be the one to start.</summary>
/// <param name="Path">Relative to the app's own folder.</param>
public sealed record ProgramChoice(string Path, string Label);

/// <summary>
/// Everything Yar found out about a file before installing it. Nothing in here was learned by
/// running the file.
/// </summary>
/// <param name="StagedPath">Where the unpacked contents wait, for archives; otherwise the file itself.</param>
public sealed record IntakeReport(
    string SourcePath,
    string FileName,
    BundleKind Kind,
    string KindLabel,
    long FileBytes,
    string Sha256,
    string SuggestedName)
{
    public string? StagedPath { get; init; }

    /// <summary>A link the file was downloaded from, if it was.</summary>
    public string? OriginUrl { get; init; }

    public string? Version { get; init; }

    public string? Summary { get; init; }

    /// <summary>A picture found for the app, as a file that can be shown. Null when none was found.</summary>
    public string? IconPath { get; init; }

    /// <summary>For packages: the app brings its own icon, which is in place once it is installed.</summary>
    public bool IconComesWithIt { get; init; }

    public IReadOnlyList<ProgramChoice> Programs { get; init; } = [];

    public long? InstalledBytes { get; init; }

    public IReadOnlyList<string> Categories { get; init; } = [];

    public IReadOnlyList<IntakeCheck> Checks { get; init; } = [];

    /// <summary>Set when this cannot be installed. Shown in place of the install button.</summary>
    public string? Blocked { get; init; }

    /// <summary>True when the user can choose the folder; false where the system decides.</summary>
    public bool FolderIsChoosable => Kind is BundleKind.AppImage or BundleKind.Program or BundleKind.Archive;

    /// <summary>Handler-specific facts, such as a package's name.</summary>
    public IReadOnlyDictionary<string, string> Technical { get; init; } = new Dictionary<string, string>();
}

/// <summary>What the user settled on in the preview.</summary>
public sealed record IntakeChoices(string Name, string Folder)
{
    /// <summary>The picture to use; null for the generic one.</summary>
    public string? IconPath { get; init; }

    /// <summary>Which of <see cref="IntakeReport.Programs"/> starts the app.</summary>
    public string? Program { get; init; }

    public bool AppMenu { get; init; } = true;

    public bool Pin { get; init; }

    /// <summary>Remove the file the user brought once the app is in place.</summary>
    public bool DeleteOriginal { get; init; }

    /// <summary>True when the file matched the hash the publisher states.</summary>
    public bool HashVerified { get; init; }
}

/// <summary>What adding an app created, for the record.</summary>
public sealed record AddedApp(string ProviderId, string AppId, string Name);

/// <summary>Takes apps the user brings as files: looks at them without running them, then installs them the standard way for their kind.</summary>
public interface IAppIntake
{
    /// <summary>Works out what the file is and whether it can run here. Changes nothing outside Yar's own cache.</summary>
    Task<IntakeReport> InspectAsync(string path, string? originUrl, CancellationToken ct);

    /// <summary>What installing with these choices would do, for the last look before it happens.</summary>
    Task<ChangePlan> PlanAsync(IntakeReport report, IntakeChoices choices, CancellationToken ct);

    Task<AddedApp> AddAsync(
        IntakeReport report, IntakeChoices choices, ChangePlan plan, IProgress<OperationProgress> progress,
        CancellationToken ct);

    /// <summary>Throws away what an inspection left in the cache, when the user decides against installing.</summary>
    void Discard(IntakeReport report);
}

/// <summary>
/// One kind of file that is installed by another part of Yar than the one that unpacks archives:
/// packages by the package manager, Flatpak files by Flatpak.
/// </summary>
public interface IFileIntakeHandler
{
    /// <summary>Fills in what this handler can tell about the file. <paramref name="basics"/> already names, sizes and hashes it.</summary>
    Task<IntakeReport> InspectAsync(IntakeReport basics, CancellationToken ct);

    Task<ChangePlan> PlanAsync(IntakeReport report, IntakeChoices choices, CancellationToken ct);

    Task<AddedApp> AddAsync(
        IntakeReport report, IntakeChoices choices, ChangePlan plan, IProgress<OperationProgress> progress,
        CancellationToken ct);
}

/// <summary>Whether a single-file app is still the file that was added.</summary>
/// <param name="Unchanged">Null when there is nothing to compare with.</param>
public sealed record IntegrityResult(bool? Unchanged, string? Sha256Now, string? Sha256WhenAdded, bool WasVerified);

/// <summary>Optional: a provider that remembers the hash of what it installed and can compare again.</summary>
public interface IIntegrityChecker
{
    Task<IntegrityResult> CheckAsync(AppInfo app, CancellationToken ct);
}

/// <summary>What a download link leads to, learned before anything is downloaded.</summary>
/// <param name="Address">Where the file really is, after any forwarding.</param>
/// <param name="Bytes">Null when the server does not say how big the file is.</param>
public sealed record LinkInfo(Uri Address, string FileName, long? Bytes);

/// <summary>Fetches a file the user gave a link to, so it can be looked at like a dropped file.</summary>
public interface IAppDownloader
{
    /// <summary>Asks the server what the link leads to. Downloads nothing.</summary>
    /// <exception cref="InvalidOperationException">The link cannot be used; the message says why.</exception>
    Task<LinkInfo> ProbeAsync(Uri link, CancellationToken ct);

    /// <summary>Downloads the file into Yar's cache and returns where it is.</summary>
    Task<string> DownloadAsync(LinkInfo link, IProgress<long> received, CancellationToken ct);

    /// <summary>Deletes a file <see cref="DownloadAsync"/> fetched, when it is no longer needed.</summary>
    void Discard(string path);
}

/// <summary>Checks a link a user typed or pasted before anything is done with it.</summary>
public static class DownloadLink
{
    /// <summary>Returns the link to use, or a plain reason why it cannot be used.</summary>
    public static (Uri? Link, string? Problem) Check(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
            return (null, null);
        if (trimmed.Length > 4096 || trimmed.Any(char.IsWhiteSpace) || !Uri.TryCreate(trimmed, UriKind.Absolute, out var link))
            return (null, "That doesn't look like a link.");
        if (link.Scheme != Uri.UriSchemeHttps)
            return (null, "I only download over a protected connection, so the link has to start with https://.");
        if (link.UserInfo.Length > 0 || link.Host.Length == 0)
            return (null, "That link carries a user name or has no server in it; I won't use it.");
        return (link, null);
    }
}
