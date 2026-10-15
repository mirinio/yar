# Changelog

What changed in each version of Yar, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/).

## [0.3.0] - 2026-10-15

The first public version, published as a pre-release.

### What it does
- **My Apps**: every installed app, from the distribution, Flathub, the Snap Store or your own downloads, grouped by where it came from, with search, categories, where each app's files and data are, and what a Flatpak app is allowed to access.
- **Store**: browse and search everything your sources offer, one entry per app with every source that offers it, pictures, ratings, reviews and a suggested source.
- **Install, update and remove** Flatpak apps and system packages. Every change shows a preview first: what comes along or goes, sizes, and warnings such as access to all your files. A removal that would take a vital system package with it is refused. An app's data is kept unless you choose to move it to the Trash.
- **Add an app you downloaded**: drop a file on the window, pick one, or paste an https link. Supported: AppImage, single programs, zip and tar archives, `.rpm` / `.deb`, `.flatpakref` / `.flatpak`. Files are checked without being run; the preview lets you set name, picture, folder, the program that starts it, app-menu entry and taskbar pin, and compare the file with the publisher's checksum. Apps added this way are removed exactly, to the Trash.
- **History**: every change made through Yar, with Undo for most of them.
- **Sources** (add Flatpak sources, turn sources on or off) and **Storage** (disks and how much each app takes).
- **`yar-apps`**: a command-line tool that does what the window does.

### Requirements
- Linux on 64-bit Intel/AMD.
- GTK 4.14 or newer and libadwaita 1.5 or newer (Fedora 40+, Ubuntu 24.04+).
- PackageKit for system packages and Flatpak for Flatpak apps; Yar works with whichever of them is there.

### Known limitations
- Not yet tried on a real desktop: the password prompt for system packages (the one for Flatpak apps has been), dropping a file with the mouse, and pinning to the taskbar.
- The window has only been used on Fedora. Installing, removing, updating and adding apps are tested in Fedora 44 and Ubuntu 24.04 containers.
- Snap apps are listed, but can't be installed or removed yet.
- Leftover data of removed apps, unused Flatpak runtimes and temporary files are shown, but there is no cleanup yet.

## 0.2.0 - 2026-10-13 (development milestone, not released)

### Added
- **Install and remove** Flatpak apps and system packages from the Store and from an app's page. Every change shows a preview first: everything that comes or goes, sizes, where it goes, and warnings such as broad access to your files.
- Removing keeps an app's data unless you switch that on; then the data goes to the Trash. A removal that would take a vital part of the system along is refused.
- **History**: every change Yar makes is recorded, with Undo where that is possible.
- One change at a time, with progress and a Cancel button in a strip at the bottom of the window.
- A throwaway test container in which installs and removals are tried for real (`scripts/test-container.sh`).

## 0.1.0 - 2026-10-13 (development milestone, not released)

### Added
- My Apps: every app in your app menu, from your distribution, Flathub, the Snap Store or your own downloads, with where it lives, its data and what it may reach, in plain words.
- Store: browse and search everything your sources offer, with pictures, ratings, reviews and a suggested source.
- Sources: understand, add and switch where your apps come from.
- Updates: see what is waiting.
- Storage: disks, unused space, and what your apps take.

[0.3.0]: https://github.com/mirinio/yar/releases/tag/v0.3.0
