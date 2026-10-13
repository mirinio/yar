using Yar.Core;

namespace Yar.Backends.PackageKit;

// Installing and removing the distribution's packages. PackageKit works out what else has to come
// or go (on Fedora a removal also takes the parts only that app needed); Yar shows that list first and refuses a removal that would take the system apart.
public sealed partial class PackageKitProvider
{
    // PK_INFO_ENUM values from pk-enum.h, as a simulated transaction reports them.
    private const uint InfoRemoving = 13, InfoObsoleting = 15, InfoUntrusted = 23;

    // Packages a working system cannot do without, by name or by the start of the name. A removal
    // whose list touches any of them is refused outright, whatever was asked for.
    private static readonly string[] Protected =
    [
        "kernel", "linux-image", "linux-modules", "systemd", "glibc", "libc6", "bash", "coreutils", "util-linux",
        "filesystem", "basesystem", "base-files", "init", "sudo", "polkit", "policykit-1", "dbus", "dbus-broker",
        "dnf", "dnf5", "libdnf5", "rpm", "apt", "dpkg", "zypper", "pacman", "PackageKit", "packagekit",
        "grub2", "grub-common", "grub-pc", "grub-efi", "shim", "NetworkManager", "network-manager",
        "plasma-workspace", "plasma-desktop", "kwin", "gnome-shell", "mutter", "gdm", "gdm3", "sddm", "lightdm",
        "xorg-x11-server-Xorg", "xserver-xorg-core", "xwayland", "mesa-dri-drivers", "gtk4", "libgtk-4-1",
        "libadwaita", "libadwaita-1-0", "fedora-release", "ubuntu-minimal", "ubuntu-desktop", "openssh-server",
    ];

    public IReadOnlyList<InstallTarget> TargetsFor(StoreOffer offer, IReadOnlyList<SourceInfo> sources) =>
        offer.ProviderId == ProviderId && IsPackageName(offer.PackageName)
            ?
            [
                new("system", "For everyone on this computer",
                    "A system package has no folder to choose: your distribution decides where each of its files goes.",
                    NeedsPassword: true),
            ]
            : [];

    public Task<ChangePlan> PlanInstallAsync(StoreOffer offer, InstallTarget target, string name, CancellationToken ct) =>
        PlanInstallAsync(offer.PackageName, name, ct);

    public Task<ChangePlan> PlanReinstallAsync(ChangeRecord removal, CancellationToken ct) =>
        PlanInstallAsync(removal.Undo.GetValueOrDefault("package"), removal.Name, ct);

    private async Task<ChangePlan> PlanInstallAsync(string? packageName, string name, CancellationToken ct)
    {
        if (!IsPackageName(packageName))
            return ChangePlan.Refused(ProviderId, packageName ?? "", name, "I can't tell exactly which package this is, so I won't guess.");

        var candidates = await _client.ResolveAvailableAsync(packageName!, ct).ConfigureAwait(false);
        if (candidates.LastOrDefault(p => p.Name == packageName) is not { } wanted)
        {
            return ChangePlan.Refused(
                ProviderId, packageName!, name,
                "None of your sources offers this right now. It may already be installed, or its source is switched off.");
        }

        var all = await _client.SimulateInstallAsync([wanted.PackageId], ct).ConfigureAwait(false);
        var sizes = await _client.GetOfferSizesAsync(
            all.Select(p => p.PackageId).Append(wanted.PackageId).Distinct(StringComparer.Ordinal).ToList(), ct)
            .ConfigureAwait(false);
        var others = all.Where(p => p.Name != wanted.Name || p.Arch != wanted.Arch).ToList();

        var blocked = Guard(all.Where(p => p.Info is InfoRemoving or InfoObsoleting).ToList(), packageName!);
        var warnings = new List<string>();
        if (all.Any(p => p.Info == InfoUntrusted))
            warnings.Add("Part of this is not signed by anyone your system knows. Your system will refuse it, and that is the safe thing.");
        var leaving = all.Where(p => p.Info is InfoRemoving or InfoObsoleting).Select(p => p.Name).ToList();
        if (leaving.Count > 0)
            warnings.Add($"Installing this also removes: {string.Join(", ", leaving)}.");

        var items = new List<PlanItem> { new(name, $"{wanted.Name} {wanted.Version}", sizes.GetValueOrDefault(wanted.PackageId)?.InstalledBytes) };
        items.AddRange(others.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new PlanItem(
            p.Name,
            p.Info is InfoRemoving or InfoObsoleting ? "Will be removed" : p.Summary.Length > 0 ? p.Summary : "Needed by it",
            sizes.GetValueOrDefault(p.PackageId)?.InstalledBytes)));

        var coming = all.Where(p => p.Info is not (InfoRemoving or InfoObsoleting)).Select(p => p.PackageId)
            .Append(wanted.PackageId).Distinct(StringComparer.Ordinal).ToList();
        return new(ProviderId, $"{wanted.Name}.{wanted.Arch}", name, items)
        {
            // A total with a part missing would be a wrong total.
            DownloadBytes = Total(coming, id => sizes.GetValueOrDefault(id)?.DownloadBytes),
            DiskBytes = Total(coming, id => sizes.GetValueOrDefault(id)?.InstalledBytes),
            NeedsPassword = true,
            Warnings = warnings,
            Notes =
            [
                "Your distribution decides where the files of a system package go, so there is no folder to choose.",
                .. others.Count > 0 ? new[] { "The other packages listed are things it needs to work. If you remove it later, your system decides which of them are still needed." } : [],
            ],
            Blocked = blocked,
            Origin = wanted.Data.Length > 0 ? wanted.Data : null,
            Technical = new Dictionary<string, string>
            {
                ["package"] = wanted.Name,
                ["package-id"] = wanted.PackageId,
            },
        };
    }

    public async Task InstallAsync(ChangePlan plan, IProgress<OperationProgress> progress, CancellationToken ct)
    {
        var id = Checked(plan);
        progress.Report(new(OperationPhase.Waiting));
        await _client.InstallAsync([id], Relay(progress, OperationPhase.Installing), ct).ConfigureAwait(false);
    }

    public string? WhyNotRemovable(AppInfo app)
    {
        if (app.ProviderId != ProviderId || !IsPackageName(app.Technical.GetValueOrDefault("package")) ||
            !app.Technical.ContainsKey("arch"))
        {
            return "I can't tell exactly which package this is, so I won't remove anything.";
        }
        return IsProtected(app.Technical["package"])
            ? "Your system needs this to work, so I won't remove it."
            : null;
    }

    public async Task<ChangePlan> PlanRemovalAsync(AppInfo app, CancellationToken ct)
    {
        if (WhyNotRemovable(app) is { } reason)
            return ChangePlan.Refused(ProviderId, app.Id, app.Name, reason);

        var packageName = app.Technical["package"];
        // Asked afresh: the version remembered with the app may have been updated since.
        var installed = await _client.ResolveInstalledAsync([packageName], ct).ConfigureAwait(false);
        if (installed.FirstOrDefault(p => p.Name == packageName && p.Arch == app.Technical["arch"]) is not { } package)
            return ChangePlan.Refused(ProviderId, app.Id, app.Name, "It is not installed any more.");

        var all = await _client.SimulateRemoveAsync([package.PackageId], ct).ConfigureAwait(false);
        var others = all.Where(p => p.Name != package.Name || p.Arch != package.Arch)
            .OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        var sizes = await _client.GetSizesAsync(others.Select(p => p.PackageId).ToList(), ct).ConfigureAwait(false);
        long? SizeOf(PkPackage p) => sizes.FirstOrDefault(pair => WithoutData(pair.Key) == WithoutData(p.PackageId)) is { Key: not null } found
            ? found.Value
            : null;

        var items = new List<PlanItem> { new(app.Name, $"{package.Name} {package.Version}", app.SizeBytes) };
        items.AddRange(others.Select(p => new PlanItem(p.Name, p.Summary.Length > 0 ? p.Summary : null, SizeOf(p))));

        var warnings = new List<string>();
        if (others.Count > 0)
        {
            warnings.Add(
                $"Removing {app.Name} also removes {others.Count} other {(others.Count == 1 ? "package" : "packages")}: " +
                "parts that only it needed, or that cannot work without it. They are listed below; make sure you don't need them.");
        }

        return new(ProviderId, app.Id, app.Name, items)
        {
            DiskBytes = app.SizeBytes is null || others.Any(p => SizeOf(p) is null)
                ? null
                : app.SizeBytes + others.Sum(p => SizeOf(p)!.Value),
            NeedsPassword = true,
            Warnings = warnings,
            Notes =
            [
                "I can't tell where this app keeps your settings, so I leave them alone.",
            ],
            Blocked = Guard(all, package.Name),
            Origin = app.SourceId,
            Technical = new Dictionary<string, string>
            {
                ["package"] = package.Name,
                ["package-id"] = package.PackageId,
                ["with-dependents"] = others.Count > 0 ? "yes" : "no",
            },
        };
    }

    public async Task<RemovalResult> RemoveAsync(
        ChangePlan plan, bool removeData, IProgress<OperationProgress> progress, CancellationToken ct)
    {
        var id = Checked(plan);
        if (IsProtected(plan.Technical["package"]))
            throw new InvalidOperationException("Your system needs this to work, so I won't remove it.");

        progress.Report(new(OperationPhase.Waiting));
        // Dependents go only if the preview listed some; otherwise the daemon refuses rather than surprise anyone.
        await _client.RemoveAsync(
            [id], allowDependents: plan.Technical.GetValueOrDefault("with-dependents") == "yes",
            Relay(progress, OperationPhase.Removing), ct).ConfigureAwait(false);
        return new([]);
    }

    /// <summary>Why a removal list must not be carried out; null when nothing in it is vital.</summary>
    internal static string? Guard(IReadOnlyList<PkPackage> leaving, string asked)
    {
        var vital = leaving.Select(p => p.Name).Where(IsProtected).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (vital.Count == 0)
            return null;
        return vital.Contains(asked, StringComparer.Ordinal) && vital.Count == 1
            ? "Your system needs this to work, so I won't remove it."
            : $"This would also remove parts your system needs to work ({string.Join(", ", vital.Take(6))}), so I won't do it.";
    }

    internal static bool IsProtected(string packageName) => Protected.Any(name =>
        packageName == name || packageName.StartsWith(name + "-", StringComparison.Ordinal));

    private static long? Total(IReadOnlyList<string> ids, Func<string, long?> sizeOf) =>
        ids.Any(id => sizeOf(id) is null) ? null : ids.Sum(id => sizeOf(id)!.Value);

    // A plan comes back from the window; check again that it names one package and nothing else.
    private static string Checked(ChangePlan plan)
    {
        var name = plan.Technical.GetValueOrDefault("package");
        var id = plan.Technical.GetValueOrDefault("package-id");
        if (plan.ProviderId != ProviderId || !IsPackageName(name) || id is null ||
            PkPackage.Parse(id, "") is not { } package || package.Name != name || id.Length > 1024 ||
            id.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            throw new InvalidOperationException("This is not a plan I made for a system package.");
        }
        return id;
    }

    private static Relayed Relay(IProgress<OperationProgress> progress, OperationPhase working) => new(progress, working);

    private sealed class Relayed(IProgress<OperationProgress> progress, OperationPhase working) : IProgress<PkProgress>
    {
        public void Report(PkProgress value) => progress.Report(new(
            value.Phase(working), value.Percentage / 100.0, Detail: null, value.AllowCancel));
    }
}
