namespace Yar.Core;

/// <summary>Who Yar is to the desktop. The menu entry, icon and metadata files under <c>data/</c> carry this name.</summary>
public static class AppIdentity
{
    /// <summary>Flathub's form for a project hosted at github.com/mirinio/yar. Renaming means this line and the files in <c>data/</c>.</summary>
    public const string Id = "io.github.mirinio.Yar";

    /// <summary>The version this build was made as, from Directory.Build.props.</summary>
    public static string Version { get; } =
        typeof(AppIdentity).Assembly.GetName().Version is { } version ? $"{version.Major}.{version.Minor}.{version.Build}" : "unknown";
}
