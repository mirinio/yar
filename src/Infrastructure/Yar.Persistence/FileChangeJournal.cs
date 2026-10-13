using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yar.Core;

namespace Yar.Persistence;

/// <summary>
/// Keeps the record of changes as one JSON object per line, usually in
/// <c>$XDG_STATE_HOME/yar/history.jsonl</c>. Lines are only ever added: a change that ends is
/// written again under the same ID, and the last line for an ID is the one that counts.
/// </summary>
public sealed class FileChangeJournal(string path) : IChangeJournal
{
    // Far more history than anyone scrolls through; a file beyond this is not read line by line for ever.
    private const long MaxBytes = 16 * 1024 * 1024;

    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerOnlyDirectory = OwnerOnlyFile | UnixFileMode.UserExecute;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16,
    };

    private readonly Lock _gate = new();

    public void Write(ChangeRecord record)
    {
        var line = JsonSerializer.Serialize(record, Options) + "\n";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!, OwnerOnlyDirectory);
                // One write call per record, appended: a line is either there in full or not at all.
                using var stream = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.Append,
                    Access = FileAccess.Write,
                    Share = FileShare.Read,
                    UnixCreateMode = OwnerOnlyFile,
                    BufferSize = 0,
                });
                stream.Write(Encoding.UTF8.GetBytes(line));
                stream.Flush(flushToDisk: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The change itself matters more than the note about it.
            }
        }
    }

    public IReadOnlyList<ChangeRecord> Read()
    {
        var latest = new Dictionary<string, ChangeRecord>(StringComparer.Ordinal);
        var order = new List<string>();
        lock (_gate)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxBytes)
                    return [];

                foreach (var line in File.ReadLines(path))
                {
                    if (Parse(line) is not { } record)
                        continue;
                    if (!latest.ContainsKey(record.Id))
                        order.Add(record.Id);
                    latest[record.Id] = record;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
        return order.Select(id => latest[id]).ToList();
    }

    // A line cut short by a crash, or edited by hand, is skipped; the others still count.
    private static ChangeRecord? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ChangeRecord>(line, Options) is { Id.Length: > 0 } record ? record : null;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
