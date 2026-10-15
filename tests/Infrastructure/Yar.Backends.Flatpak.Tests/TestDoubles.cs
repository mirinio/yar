using Yar.Core;

namespace Yar.Backends.Flatpak.Tests;

/// <summary>Stands in for the flatpak command: records what it was asked and answers as told.</summary>
internal sealed class FakeRunner(Func<IReadOnlyList<string>, ProcessResult> respond) : IProcessRunner
{
    /// <summary>Answers every command with success and no output.</summary>
    public FakeRunner()
        : this(_ => new(0, "", ""))
    {
    }

    public List<IReadOnlyList<string>> Calls { get; } = [];

    public int SetUpCalls { get; private set; }

    public Task<ProcessResult> RunAsync(
        string absolutePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        // The one command the provider runs first, to let flatpak create its folders.
        if (arguments is ["remotes"])
        {
            SetUpCalls++;
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
        lock (Calls)
            Calls.Add(arguments);
        return Task.FromResult(respond(arguments));
    }
}

/// <summary>A Trash that only notes what was moved into it.</summary>
internal sealed class FakeTrash : ITrash
{
    public List<string> Moved { get; } = [];
    public string? WhyNot(string path) => null;
    public bool Holds(TrashedItem item) => true;
    public void Restore(TrashedItem item) { }

    public TrashedItem Move(string path)
    {
        Moved.Add(path);
        return new(path, "/t/files/x", "/t/info/x.trashinfo");
    }
}

/// <summary>Keeps every progress report, in order.</summary>
internal sealed class Collect<T>(List<T> into) : IProgress<T>
{
    public void Report(T value) => into.Add(value);
}
