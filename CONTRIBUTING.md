# Contributing to Yar

Thank you for wanting to help. Yar is young, so every bug report, test on a distribution
nobody has tried yet, and careful review is worth a lot.

## Ways to help without writing code

- **Try it on your distribution** and tell us what worked and what didn't. Yar is developed
  on Fedora; Ubuntu is only tested in a container, and openSUSE, Arch and Snap have not been
  tried at all.
- **Report bugs** with the bug template. The output of `yar-apps status` and
  `yar-apps --version` helps a lot.
- **Read the code that changes your system** and tell us what you think: installing,
  removing, unpacking archives, the Trash. See [SECURITY.md](SECURITY.md) for what Yar
  promises there.
- **Improve the wording.** Yar talks to people who may be new to Linux. If a sentence
  confused you, it will confuse others.

## Building and testing

You need the .NET 10 SDK, GTK 4.14+ and libadwaita 1.5+.

```bash
dotnet build
dotnet test
dotnet format --verify-no-changes     # the same check CI runs
dotnet run --project src/Presentation/Yar.Gtk          # the app
dotnet run --project src/Presentation/Yar.Cli -- apps  # the same data on the command line
```

PackageKit is reached over D-Bus through generated code in
`src/Infrastructure/Yar.Backends.PackageKit/DBus/`. If its interface files change, regenerate it with
the tool pinned in `dotnet-tools.json`:

```bash
dotnet tool restore
D=src/Infrastructure/Yar.Backends.PackageKit/DBus
dotnet dbus codegen --protocol-api --namespace Yar.Backends.PackageKit.DBus --service-name PackageKit \
  --output $D/PackageKit.DBus.cs $D/Interfaces/org.freedesktop.PackageKit.Transaction.xml $D/Interfaces/org.freedesktop.PackageKit.xml
```

## Never test installs on a real computer

Anything that installs, removes or changes software is tried in a throwaway container,
never on a computer someone uses:

```bash
scripts/test-container.sh --self-check             # Fedora: install, remove, undo, add, update, for real
scripts/test-container.sh --distro ubuntu --self-check
scripts/test-container.sh                          # a shell inside it, with yar-apps on the path
```

Everything inside runs as root, which is what lets PackageKit act without a password
prompt. That is only acceptable there: Yar itself never runs as root. If you add something
that changes a system, add a step for it to `scripts/container/self-check.sh`.

## How the code is laid out

Yar follows a hexagonal layout: the core knows nothing about GTK, D-Bus or the file system.

| Folder | What lives there |
|---|---|
| `src/Core/Yar.Core` | Domain models, the ports (interfaces), the catalog, the record of changes, undo |
| `src/Infrastructure/*` | One project per kind of software (PackageKit, Flatpak, Snap, downloaded apps), plus metadata, online data and persistence |
| `src/Presentation/Yar.Gtk` | The GTK 4 / libadwaita app, one folder per sidebar tab |
| `src/Presentation/Yar.Cli` | `yar-apps`, a command-line tool that does what the app does |
| `src/Composition/Yar.Hosting` | Wires it all together |
| `tests/` | Mirrors `src/` |

A provider can do more than list apps by implementing optional interfaces such as
`IAppInstaller`, `IAppRemover` or `IAppUpdater`. Every change to the system goes through
`AppChanges`, which records it and runs one change at a time.

## Rules for code that changes the system

- Show a preview first, and carry out exactly the plan that was shown.
- Never run as root and never build shell command strings; pass arguments as a list.
- Never run a downloaded file to find out what it is.
- Move to the Trash instead of deleting, and only what Yar can be sure belongs to the app.
- Check a plan again when it comes back from the window, as if it had been tampered with.
- Write a test for the hostile case, not just the happy one.

## Code style

- `dotnet format` and `.editorconfig` decide formatting; CI fails if they disagree.
- Warnings are errors. Fix them instead of silencing them; where a rule truly does not
  apply, silence it in the smallest place with a comment saying why.
- Comments explain *why*, not *what*.

## Words the user sees

Yar speaks plainly, in the first person, and honestly about risk. In short:

- Write for someone who has never heard the word "repository".
- Say what will happen and what it means for the user, not how it works inside.
- Never hide a risk to make something sound friendlier.
- Every user-visible string goes through `T._()` or `T.F()` so it can be translated later.

## Commits and pull requests

- One topic per commit. The first line says what changed, in plain words; the body says
  why and anything a reviewer should know, including what was *not* tested.
- Open an issue first for larger changes, so we can agree on the approach before you spend
  time on it.
- The pull request template asks how you tested. "Unit tests only" is a fine answer for
  some changes; say so honestly.

## Code of conduct

Everyone taking part follows the [Code of Conduct](CODE_OF_CONDUCT.md).
