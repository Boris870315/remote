using System.Diagnostics;

namespace Remote.Infrastructure.Processes;

/// <summary>Starts a platform application without invoking an intermediate command shell.</summary>
public sealed class SystemProcessLauncher : IProcessLauncher
{
    public Task<LaunchedProcess> LaunchAsync(
        ExternalLaunchSpec specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = specification.FileName,
            UseShellExecute = specification.UseShellExecute,
        };
        foreach (var argument in specification.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{specification.FileName}'.");
        return Task.FromResult(new LaunchedProcess(process.Id));
    }
}
