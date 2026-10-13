using Yar.Core;
using Yar.Hosting;

namespace Yar.App;

/// <summary>What every page needs: the data being shown and ways to move around the window.</summary>
internal sealed class Ui(
    Gtk.Window window,
    YarHost host,
    Settings settings,
    CatalogSnapshot snapshot,
    Presentation presentation,
    Action<Adw.NavigationPage> push,
    Action<string> toast,
    Action refresh,
    Action openSettings)
{
    public Gtk.Window Window => window;
    public YarHost Host => host;
    public CatalogSnapshot Snapshot => snapshot;
    public Presentation Presentation => presentation;
    public bool Technical => settings.ShowTechnicalDetails;

    /// <summary>False when the user switched off ratings, reviews and popular lists from the internet.</summary>
    public bool OnlineAllowed => settings.UseOnlineData;

    /// <summary>The languages to show trending projects for; empty when the developer section is off.</summary>
    public IReadOnlyList<string> DeveloperLanguages =>
        settings is { UseOnlineData: true, ShowDeveloperProjects: true, DeveloperLanguages: { } languages } ? languages : [];

    public void Toast(string text) => toast(text);

    /// <summary>Looks at the system again, after something was changed from inside Yar.</summary>
    public void Refresh() => refresh();

    public void Push(Adw.NavigationPage page) => push(page);

    public void OpenSettings() => openSettings();

    public void ShowApp(AppInfo app) => push(AppDetailsPage.Create(this, app));

    public void ShowSource(SourceInfo source) => push(SourcePage.Create(this, source));

    public void ShowKind(string providerId) => push(KindPage.Create(this, providerId));

    /// <summary>Goes to the page of where an app came from: its source if known, otherwise its kind.</summary>
    public void ShowOrigin(AppInfo app)
    {
        if (presentation.SourceOf(app) is { } source)
            ShowSource(source);
        else
            ShowKind(app.ProviderId);
    }
}
