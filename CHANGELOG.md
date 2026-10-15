# Changelog

What changed in each version of Yar, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/).

## [0.3.0] - 2026-10-15

The first public version.

### Added
- **Add an app you downloaded.** Drop a file on the window, pick one, or paste an https link: AppImage, single program, zip or tar archive, RPM or Debian package, Flatpak file. Yar reads the file to find out what it is and whether it can run on this computer, without ever running it, and shows a preview you can adjust: name, picture, folder, which program starts it, app-menu entry, taskbar pin.
- **Is it the genuine file?** Paste the hash the publisher states; a file that doesn't match is not installed. The file's SHA-256 is kept with the app and can be checked again later.
- Apps added from a file are removed exactly, to the Trash, from a record of what was created.
- Windows and Mac programs, installer scripts, source code, Snap files and web pages are explained instead of installed.
- **Updates**: update one app, only your apps, or everything, each behind a preview of what comes along.
- Pin an added app to the taskbar on Plasma and GNOME.
- "Apps you add yourself" in the settings: where those apps go, with the alternatives explained.
- The Store says how to install an app no source offers, and pictures open large when clicked.

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
