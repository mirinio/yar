using System.Diagnostics;
using Yar.Core;
using Yar.Metadata;

namespace Yar.App;

/// <summary>Starts an installed app the way the app menu would.</summary>
internal static class AppLauncher
{
    public static void Start(Ui ui, AppInfo app, string launcherPath)
    {
        try
        {
            var entry = DesktopEntryParser.ParseFile(launcherPath, Path.GetFileName(launcherPath));
            if (entry?.GetKey("Terminal") == "true")
            {
                ui.Toast(T.F("{0} runs in a terminal window, which I can't open yet.", app.Name));
                return;
            }

            // Field codes such as %U stand for files to open; started from here there are none.
            // "%%" is how an entry writes a real percent sign.
            var arguments = DesktopExec.Split(entry?.Exec)?
                .Where(argument => !(argument.Length == 2 && argument[0] == '%' && argument[1] != '%'))
                .Select(argument => argument.Replace("%%", "%", StringComparison.Ordinal))
                .ToList();
            if (arguments is not { Count: > 0 } ||
                DesktopExec.FindProgram(arguments[0], ui.Host.SearchPath) is not { } program)
            {
                ui.Toast(T.F("Hmm, I couldn't start {0}: its program is missing.", app.Name));
                return;
            }

            // No shell in between: the program and each argument are passed on exactly as written.
            // Apps expect to start in the folder their entry names, or else in the home folder,
            // not in whatever folder Yar itself was started from.
            var folder = entry?.GetKey("Path") is { Length: > 0 } path && Directory.Exists(path) ? path : ui.Host.Dirs.Home;
            var info = new ProcessStartInfo(program) { UseShellExecute = false, WorkingDirectory = folder };
            foreach (var argument in arguments.Skip(1))
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            ui.Toast(T.F("Starting {0}…", app.Name));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            ui.Toast(T.F("Hmm, I couldn't start {0}.", app.Name));
        }
    }
}
