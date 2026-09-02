using System.Text.Json;
using Remote.Infrastructure.Diagnostics;

namespace Remote.Application.Tests;

public sealed class LocalErrorLogTests
{
    [Fact]
    public async Task WriteAsync_WritesStructuredSafeEventAndRotates()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"remote-log-{Guid.NewGuid():N}");
        var path = System.IO.Path.Combine(root, "errors.jsonl");
        try
        {
            var log = new LocalErrorLog(path, maximumBytes: 1);
            await log.WriteAsync("RDP", "connection-timeout", "Host 192.0.2.1 timed out", "TimeoutException");
            await log.WriteAsync("SSH2", "host-key", "Host key changed", "InvalidOperationException");

            Assert.True(File.Exists(path));
            Assert.True(File.Exists(path + ".1"));
            using var document = JsonDocument.Parse((await File.ReadAllLinesAsync(path)).Single());
            Assert.Equal("host-key", document.RootElement.GetProperty("code").GetString());
            Assert.False(document.RootElement.TryGetProperty("password", out _));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
