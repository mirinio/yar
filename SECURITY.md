# Security

Yar manages the software on your computer, so it sits on a sensitive path. This page says
how to report a problem, and what Yar does to keep you safe.

## Reporting a vulnerability

Please **don't open a public issue** for a security problem. Report it privately through
GitHub instead: on the repository page, open **Security → Report a vulnerability**.

You'll get an answer within a week. Please give us a reasonable time to fix the problem
before you talk about it publicly.

## Supported versions

Yar is in early development. Only the newest release gets security fixes.

| Version | Supported |
|---|---|
| 0.3.x | Yes |
| older | No |

## What Yar promises

- **It never runs as root.** Installing system packages goes through PackageKit, and your
  system asks for your password in its own dialog. Yar never sees your password.
- **Nothing changes without a preview.** Every install, removal and update first shows
  everything it involves, and Yar carries out exactly that plan.
- **It never runs a downloaded file to inspect it.** Files you bring are identified by
  reading them. An AppImage's name and picture are read with `unsquashfs`, not by starting it.
- **Archives can't escape.** Entries with `..` or absolute paths are refused, nothing is
  written through a link, links pointing outside the app are dropped, set-user-ID bits are
  removed, and size and file count are capped. Archives are unpacked into Yar's own
  temporary folder first.
- **Menu entries are written by Yar**, never copied from a downloaded app, with the program
  path checked and quoted.
- **No shell commands.** External tools are started by absolute path with their arguments
  as a list, so file names can't change what is run.
- **Removing goes to the Trash.** Only files Yar recorded when it added an app, or that are
  certainly the app's own, are touched. A removal that would take a vital part of your
  system along is refused.
- **Downloads are https only**, also when the server forwards to another address. You can
  compare a download with the hash its publisher states; a file that doesn't match is not
  installed.
- **Package files you bring** (`.rpm`, `.deb`) are installed only after a separate warning,
  because nobody has checked them and they install with administrator rights.
- **No telemetry.** Ratings, reviews and pictures are fetched from public services only while
  "online data" is switched on in the settings, and Yar sends nothing about you.

## What has not been checked

Yar is tested with unit tests and in throwaway Fedora and Ubuntu containers. These parts
have not yet been tried on a real desktop: the system's password prompt, dropping a file
with the mouse, and pinning to the taskbar. Reviews of the code that changes the system are
very welcome.
