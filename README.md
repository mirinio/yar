# Yar

**Your friend for apps on Linux.**

*Yar* (یار) means "friend" in Persian. It's an app manager that shows everything
installed on your Linux system — from your distribution, from Flathub, from the Snap
Store, or from a file you downloaded yourself — in one friendly place.

> **Status: early development (0.1, read-only).** Yar can show your apps and where
> they come from. It cannot install, update or remove anything yet.

## What works today

- **Storage**: how full each disk is, which parts of a disk Linux is not using, and a block picture of what your apps take: each app with its data, the parts Flatpak apps share, temporary files and data left behind. For everything else it hands over to Filelight or Disk Usage Analyzer. It measures; it removes nothing
- **Worth a look**: a note in My Apps when something could be tidied: the same app installed twice, menu entries whose program is gone, apps that are not in your menu, and data left behind by removed Flatpak apps. It shows and explains; it removes nothing yet
- **My Apps**: every app in your app menu, split into "From sources" and "Installed myself", with category tags, search and filters
- **App pages**: an Open button, where the app came from, its version and size, where it lives and where it keeps your data, and what it is allowed to reach on your computer, in plain words
- **Sources**: the kinds of software on your computer (distribution packages, Flatpak, Snap, your own downloads). Each explains what it is, links to its website, shows where it keeps things, lists its sources, and shows which of your apps came through it
- **Store**: a front page with this week's picks, shortcuts by kind of app, and rows of what is trending, most downloaded and recently updated; search across everything your sources offer. An app offered by several sources is listed once. Its page shows pictures, ratings, reviews, how often it was installed, every source that has it with download and installed size, which one Yar suggests, and what the app would be allowed to reach. Yar does not install yet; a button hands over to your software centre
- **Open Source tab** (off unless you switch it on): new GitHub projects that are gathering stars, by programming language and period
- **Adding sources**: a built-in list of popular sources and a way to add a Flatpak source by its address, each marked with how well Yar knows it. Sources that are already set up can be switched on and off, right in the list
- **Software from outside your distribution**: on Fedora, the settings hold the one yes-or-no choice for Fedora's prepared third-party sources (Chrome, Steam, NVIDIA drivers, full Flathub), with a plain explanation of open source, closed source and mixed software behind a "?"
- **Updates**: what is waiting, app updates by name and system updates grouped, with security fixes marked. Yar shows them; installing still happens in Discover or GNOME Software
- **Starts at once**: Yar shows the list it saved last time, checks it in the background, and updates itself when you install or remove something while it is open
- "?" buttons explain terms a newcomer may not know; "Show technical details" adds package names and IDs
- Works through PackageKit, so the same code covers dnf, apt, zypper and pacman

Developed on Fedora 44. The command-line tool has also been run in an Ubuntu 24.04
container: apps, their origins, sources and the Store work there. Snap, the app window
itself, openSUSE and Arch have not been tried yet.

## Build and run

You need the .NET 10 SDK, GTK 4.14 or newer and libadwaita 1.5 or newer.

```bash
dotnet build
dotnet test
dotnet run --project src/Presentation/Yar.Gtk        # the app
dotnet run --project src/Presentation/Yar.Cli -- apps   # the same data on the command line
dotnet run --project src/Presentation/Yar.Cli -- status --timings   # how long each part of a load takes
```

`scripts/publish.sh` builds a self-contained tarball that runs without .NET installed.

## How it is built

| Layer | Project | What it does |
|---|---|---|
| Core | `Yar.Core` | Domain models, the ports (interfaces) and the catalog that combines providers |
| Infrastructure | `Yar.Backends.PackageKit` | Distribution packages and repositories, over D-Bus |
| Infrastructure | `Yar.Backends.Flatpak` | Flatpak remotes and apps |
| Infrastructure | `Yar.Backends.Snap` | Installed snaps, from the snapd socket |
| Infrastructure | `Yar.Backends.Bundles` | AppImages and launchers you set up yourself |
| Infrastructure | `Yar.Online` | Ratings, reviews and popular lists from the internet; the only part that uses the network |
| Infrastructure | `Yar.Persistence` | Saved results in `~/.cache/yar` that make the next start fast; safe to delete |
| Infrastructure | `Yar.Metadata` | Desktop entries, AppStream data, the table of known sources |
| Presentation | `Yar.Gtk` | The GTK 4 / libadwaita app |
| Presentation | `Yar.Cli` | `yar-apps`, a developer tool that prints what the backends see |
| Composition | `Yar.Hosting` | Wires everything together |

The folders under `src/` follow those layers. Dependencies only point towards `Core`.

Yar never runs as root. It changes your system in exactly four ways: adding a Flatpak source for your own user, removing one you added, switching an existing source on or off, and recording your yes or no to Fedora's third-party sources. The last one runs Fedora's own `fedora-third-party` tool through `pkexec`, using the permission rule Fedora ships for it; whenever a change reaches beyond your own user, your system asks for your password. Everything else only reads.

**What goes over the internet**, all of it behind one switch in the settings:

| What | From | What Yar sends |
|---|---|---|
| Star ratings and reviews | odrs.gnome.org | For reviews: the app, your language, your distribution's name, and a throwaway random ID |
| Picks, trending, most downloaded, install counts, sizes and permissions | flathub.org | For counts, sizes and permissions: the app's ID |
| Pictures of apps | dl.flathub.org, dl.fedoraproject.org, appstream.ubuntu.com, appstream.debian.org | The picture's address. No other servers are contacted for pictures, whatever a catalog names |
| New GitHub projects (only if switched on) | api.github.com | The programming languages you ticked. No account or token |

Lists are fetched at most once a day, GitHub at most every six hours, and pictures are kept in `~/.cache/yar/images`.

## License

GPL-3.0-or-later. See [`LICENSE`](LICENSE).
