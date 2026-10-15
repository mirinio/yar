# Yar

**Your friend for apps on Linux.**

*Yar* (یار) means "friend" in Persian. It's an app manager that shows everything
installed on your Linux system — from your distribution, from Flathub, from the Snap
Store, or from a file you downloaded yourself — in one friendly place.

> **Status: early development.** Yar shows your apps, where they come from, what is
> on offer and where your disk space goes. It installs and removes Flatpak apps and
> system packages, adds apps you bring as a file or a link, and keeps a history of
> what it changed, with undo.

## What works today

- **Install and remove**: an Install button on every offer in the Store and a Remove button on an app's page, for Flatpak apps and for your distribution's packages. Each opens a preview first: everything that will come or go, how much room it takes, where it goes, and anything to be aware of, such as an app that asks for access to all your files. Removing keeps an app's settings unless you say otherwise, and then they go to the Trash. Yar refuses a removal that would take a vital part of your system along
- **Add an app you downloaded**: drop a file on the window, pick one, or paste a download link (AppImage, zip or tar archive, RPM or Debian package, Flatpak file). Yar works out what it is by reading it, never by running it, tells you whether it can run on this computer, and shows a preview you can adjust: name, picture, folder, which program starts it, app-menu entry, taskbar pin. Paste the publisher's hash and Yar tells you whether you have the genuine file. Windows programs, installer scripts and source code are explained instead of installed
- **History**: everything Yar changed for you, newest first, with Undo where that is possible and the reason where it is not. A strip at the bottom of the window shows a change while it runs, with a Cancel button

- **Storage**: how full each disk is, which parts of a disk Linux is not using, and a block picture of what your apps take: each app with its data, the parts Flatpak apps share, temporary files and data left behind. For everything else it hands over to Filelight or Disk Usage Analyzer. It measures; it removes nothing
- **Worth a look**: a note in My Apps when something could be tidied: the same app installed twice, menu entries whose program is gone, apps that are not in your menu, and data left behind by removed Flatpak apps. It shows and explains; it removes nothing yet
- **My Apps**: every app in your app menu, split into "From sources" and "Installed myself", with category tags, search and filters
- **App pages**: an Open button, where the app came from, its version and size, where it lives and where it keeps your data, and what it is allowed to reach on your computer, in plain words
- **Sources**: the kinds of software on your computer (distribution packages, Flatpak, Snap, your own downloads). Each explains what it is, links to its website, shows where it keeps things, lists its sources, and shows which of your apps came through it
- **Store**: a front page with this week's picks, shortcuts by kind of app, and rows of what is trending, most downloaded and recently updated; search across everything your sources offer. An app offered by several sources is listed once. Its page shows pictures, ratings, reviews, how often it was installed, every source that has it with download and installed size, which one Yar suggests, and what the app would be allowed to reach
- **Open Source tab** (off unless you switch it on): new GitHub projects that are gathering stars, by programming language and period
- **Adding sources**: a built-in list of popular sources and a way to add a Flatpak source by its address, each marked with how well Yar knows it. Sources that are already set up can be switched on and off, right in the list
- **Software from outside your distribution**: on Fedora, the settings hold the one yes-or-no choice for Fedora's prepared third-party sources (Chrome, Steam, NVIDIA drivers, full Flathub), with a plain explanation of open source, closed source and mixed software behind a "?"
- **Updates**: what is waiting, app updates by name and system updates grouped, with security fixes marked. Update one app from its row or its page, only your apps, or everything at once; each shows a preview first, including what comes along and whether to restart afterwards. Updates can't be undone, and the history says so
- **Starts at once**: Yar shows the list it saved last time, checks it in the background, and updates itself when you install or remove something while it is open
- "?" buttons explain terms a newcomer may not know; "Show technical details" adds package names and IDs
- Works through PackageKit, so the same code covers dnf, apt, zypper and pacman

Developed on Fedora 44. Installing, removing, undoing and adding apps from files are
exercised for real through the command-line tool in throwaway Fedora and Ubuntu 24.04
containers. Not tried yet: the system's password prompt on a real desktop (the container
runs as root), dropping a file with the mouse, taskbar pinning on a live desktop, Snap,
the app window on anything but Fedora, and openSUSE and Arch.

## Build and run

You need the .NET 10 SDK, GTK 4.14 or newer and libadwaita 1.5 or newer.

```bash
dotnet build
dotnet test
dotnet run --project src/Presentation/Yar.Gtk        # the app
dotnet run --project src/Presentation/Yar.Cli -- apps   # the same data on the command line
dotnet run --project src/Presentation/Yar.Cli -- status --timings   # how long each part of a load takes
dotnet run --project src/Presentation/Yar.Cli -- inspect some.AppImage   # what a file is and whether it can run here
dotnet run --project src/Presentation/Yar.Cli -- install org.gnome.Calculator   # prints the plan; changes nothing without --yes
```

`scripts/publish.sh` builds a self-contained tarball that runs without .NET installed.

`scripts/install-local.sh` puts Yar into your app menu, for your user only and without a
password: the program under `~/.local/opt/yar`, a `yar` command, a menu entry and an icon.
`scripts/uninstall-local.sh` removes exactly those again.

### Testing things that change a system

Installing and removing software should not be tried out on a computer someone uses.
`scripts/test-container.sh` starts a throwaway Fedora container (add `--distro ubuntu`
for Ubuntu 24.04) with Flatpak and PackageKit running and `yar-apps` on the path:

```bash
scripts/test-container.sh --self-check   # install, remove, undo and add apps for real, through Yar
scripts/test-container.sh                # a shell inside it
scripts/test-container.sh -- yar-apps apps
```

Inside it everything runs as root, which is what lets PackageKit act without a password
prompt. That is only acceptable there; Yar never runs as root on a real system. It needs
`podman`, and the container is deleted when it exits.

## How it is built

| Layer | Project | What it does |
|---|---|---|
| Core | `Yar.Core` | Domain models, the ports (interfaces) and the catalog that combines providers |
| Infrastructure | `Yar.Backends.PackageKit` | Distribution packages and repositories, over D-Bus |
| Infrastructure | `Yar.Backends.Flatpak` | Flatpak remotes and apps |
| Infrastructure | `Yar.Backends.Snap` | Installed snaps, from the snapd socket |
| Infrastructure | `Yar.Backends.Bundles` | AppImages and launchers you set up yourself; looking at, unpacking and installing apps brought as files |
| Infrastructure | `Yar.Online` | Ratings, reviews and popular lists from the internet, and downloading a file from a link you gave; the only part that uses the network |
| Infrastructure | `Yar.Persistence` | Saved results in `~/.cache/yar` that make the next start fast (safe to delete), and the record of changes in `~/.local/state/yar` (not safe to delete: it is the history and what undo works from) |
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
