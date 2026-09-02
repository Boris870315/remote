using System.Text.Json;

namespace Remote.Infrastructure.Diagnostics;

/// <summary>Writes bounded, secret-free diagnostic events to a local JSON Lines file.</summary>
public sealed class LocalErrorLog(string path, long maximumBytes = 2 * 1024 * 1024)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public async Task WriteAsync(
        string category,
        string code,
        string safeMessage,
        string? exceptionType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            RotateIfNeeded();
            var line = JsonSerializer.Serialize(new ErrorEvent(
                DateTimeOffset.UtcNow,
                "Error",
                category,
                code,
                safeMessage,
                exceptionType), SerializerOptions);
            await File.AppendAllTextAsync(Path, line + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(Path) || new FileInfo(Path).Length < maximumBytes)
        {
            return;
        }

        var oldest = $"{Path}.3";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var index = 2; index >= 1; index--)
        {
            var source = $"{Path}.{index}";
            if (File.Exists(source)) File.Move(source, $"{Path}.{index + 1}", true);
        }
        File.Move(Path, $"{Path}.1", true);
    }

    private sealed record ErrorEvent(
        DateTimeOffset TimestampUtc,
        string Severity,
        string Category,
        string Code,
        string Message,
        string? ExceptionType);
}
