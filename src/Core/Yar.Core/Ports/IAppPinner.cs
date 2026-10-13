namespace Yar.Core;

/// <summary>Puts an app's launcher on the desktop's taskbar or dock, where the desktop offers a way to do that.</summary>
public interface IAppPinner
{
    /// <summary>Why pinning is not possible on this desktop, in plain words; null when it is.</summary>
    string? WhyNot { get; }

    /// <param name="desktopId">The launcher's file name, such as <c>yar-krita.desktop</c>.</param>
    /// <exception cref="InvalidOperationException">The desktop refused or could not be asked; the message says why.</exception>
    Task PinAsync(string desktopId, CancellationToken ct);

    /// <summary>Takes the launcher off again. Not being pinned is not an error.</summary>
    Task UnpinAsync(string desktopId, CancellationToken ct);
}
