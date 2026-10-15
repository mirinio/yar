<div align="center">

<img src="assets/cube_logo.svg" width="96" alt="Yar's logo: a cube">

# Yar

**Your friend for apps on Linux.**

[![CI](https://github.com/mirinio/yar/actions/workflows/ci.yml/badge.svg)](https://github.com/mirinio/yar/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/mirinio/yar?include_prereleases)](https://github.com/mirinio/yar/releases)
[![License: GPL v3+](https://img.shields.io/badge/license-GPL--3.0--or--later-blue)](LICENSE)
![Linux](https://img.shields.io/badge/platform-Linux-informational)

</div>

On Linux, apps come from many places: your distribution, Flathub, the Snap Store, or a file
you downloaded from a website. Each is managed differently, and after a while nobody knows
what is installed where, or how to get rid of it cleanly.

**Yar** puts all of it in one friendly place. It is made for people coming from Windows who
want to see their apps, install and remove them, and understand what is going on, in plain
words.

<p align="center">
  <img src="docs/demo.gif" alt="Yar: browsing the Store, the preview before installing, progress, and the history" width="880">
</p>

## Why Yar

- **Everything, from anywhere, in one list.** Distribution packages, Flatpak, Snap, AppImages
  and apps you unpacked yourself, each with where it came from, where it lives and what it may reach.
- **Downloaded apps, done properly.** Drop an AppImage, an archive or a package on the window:
  Yar checks it without running it, sets it up with a menu entry, and can remove it cleanly
  later, because it wrote down what it created.
- **Nothing happens by surprise.** Every install, removal and update shows a preview first,
  and the history can undo most changes.
- **Honest, plain language.** Yar explains terms a newcomer may not know, and says so when
  something is risky, like an app that can read all your files.

## Screenshots

<table>
<tr>
<td width="50%">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/my-apps-dark.png">
  <img src="docs/screenshots/my-apps-light.png" alt="My Apps: every installed app, wherever it came from">
</picture>

**My Apps**: everything installed, wherever it came from.
</td>
<td width="50%">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/store-app-dark.png">
  <img src="docs/screenshots/store-app-light.png" alt="An app in the Store with its sources and an Install button">
</picture>

**Store**: each app once, with every source that offers it.
</td>
</tr>
<tr>
<td>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/install-preview-dark.png">
  <img src="docs/screenshots/install-preview-light.png" alt="The preview before installing, with a warning about broad access">
</picture>

**Preview**: what comes along, how big it is, what to know.
</td>
<td>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/add-app-dark.png">
  <img src="docs/screenshots/add-app-light.png" alt="Adding an app from a downloaded archive, with the hash check">
</picture>

**Add an app**: from a file or a link, checked first.
</td>
</tr>
<tr>
<td>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/history-dark.png">
  <img src="docs/screenshots/history-light.png" alt="The history of changes, with Undo">
</picture>

**History**: everything Yar changed, with Undo.
</td>
<td>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/updates-dark.png">
  <img src="docs/screenshots/updates-light.png" alt="Waiting updates, one at a time or all at once">
</picture>

**Updates**: one app, your apps, or everything.
</td>
</tr>
</table>

## What it does

- **My Apps**: every app in your app menu, split into "from sources" and "installed myself",
  with category tags and search. Each app's page shows where it came from, its version and
  size, where its files and data are, and what it may reach on your computer.
- **Store**: browse and search everything your sources offer, with pictures, ratings,
  reviews and install counts. An app offered by several sources is listed once, with the
  differences explained and one suggested.
- **Install, update and remove** Flatpak apps and your distribution's packages. Removing keeps
  an app's data unless you say otherwise, and then it goes to the Trash.
- **Add an app you downloaded**: AppImage, single program, zip or tar archive, RPM or Debian
  package, Flatpak file, or a download link. You can adjust the name, picture, folder and
  taskbar pin, and compare the file with the hash its publisher states.
- **Updates**: what is waiting, with security fixes marked; update one app, your apps, or everything.
- **History**: every change, with Undo where that is possible.
- **Sources**: understand, add and switch where your apps come from, with how well Yar knows each one.
- **Storage**: disks, unused space, and a picture of what your apps take, with their data.

## Safe by design

Yar manages the software on your computer, so it is careful:

- It **never runs as root**. Your system asks for your password in its own dialog when needed.
- It **never runs a downloaded file** to find out what it is.
- It **moves to the Trash** instead of deleting, and refuses a removal that would take a vital
  part of your system along.
- It **shows a preview before every change** and carries out exactly what it showed.
- It sends **nothing about you** anywhere. Online extras (ratings, pictures) can be switched off.

More in [SECURITY.md](SECURITY.md).

## Try it

**From a release**: download `yar-<version>-linux-x64.tar.gz` from
[Releases](https://github.com/mirinio/yar/releases), check it against the `.sha256` file next
to it, then:

```bash
tar -xzf yar-*-linux-x64.tar.gz
cd yar-*-linux-x64
./install-local.sh        # for your user only, no password; ./uninstall-local.sh removes it again
```

Yar is then in your app menu. It needs **GTK 4.14+** and **libadwaita 1.5+**, which current
distributions have.

**From source**, with the .NET 10 SDK:

```bash
git clone https://github.com/mirinio/yar.git && cd yar
dotnet run --project src/Presentation/Yar.Gtk
```

## Where it works

| | Status |
|---|---|
| Fedora 44 (KDE Plasma) | Developed and used here, app and command line |
| Ubuntu 24.04 | Everything that changes the system tested in a container; the app window not yet tried |
| openSUSE, Arch, other distributions | Not tried yet; Yar talks to PackageKit, so they may well work |
| Snap | Apps are listed; installing and removing snaps is not supported yet |
| Not yet tried on a real desktop | The system's password prompt, dropping a file with the mouse, pinning to the taskbar |

Tried it somewhere else? Please [tell us](https://github.com/mirinio/yar/issues).

## Status and roadmap

Yar is young: **0.3.0** is the first public version. Next up are cleaning up what removed apps
left behind, unused Flatpak runtimes and temporary files, through the same preview → Trash →
undo path, and wider testing on other distributions. The [changelog](CHANGELOG.md) lists what
changed in each version.

## What goes over the internet

All of it is behind one switch in the settings:

| What | From | What Yar sends |
|---|---|---|
| Star ratings and reviews | odrs.gnome.org | For reviews: the app, your language, your distribution's name, a throwaway random ID |
| Picks, trending, install counts, sizes, permissions | flathub.org | The app's ID |
| Pictures of apps | dl.flathub.org, dl.fedoraproject.org, appstream.ubuntu.com, appstream.debian.org | The picture's address; no other servers are contacted for pictures |
| New GitHub projects (only if switched on) | api.github.com | The programming languages you ticked; no account |

A download link you paste yourself is fetched from wherever it points, over https only.

## How it is built

Yar is written in C# on .NET 10, with GTK 4 and libadwaita through
[Gir.Core](https://gircore.github.io). It talks to PackageKit over D-Bus, so the same code
covers dnf, apt, zypper and pacman, and to Flatpak and snapd directly. The code follows a
hexagonal layout: a core that knows nothing about the desktop, one project per kind of
software, and the app and the command-line tool `yar-apps` on top.
[CONTRIBUTING.md](CONTRIBUTING.md) explains the layout, how to build and test, and the
throwaway container in which installs are tried for real.

## Made with an AI assistant

Yar is written with Claude, Anthropic's AI assistant, under its maintainer's direction, and
checked with automated tests and real installs in throwaway containers. [AI.md](AI.md) says
who does what and how the work is checked.

## The name

*Yar* (یار) is Persian for "friend", and *yaar* means "buddy" in Hindi and Urdu: an app that
looks after your apps, so you don't have to know what a repository is.

## License

Yar is free software under the [GNU General Public License, version 3 or later](LICENSE).
