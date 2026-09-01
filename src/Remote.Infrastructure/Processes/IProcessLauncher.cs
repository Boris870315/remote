namespace Remote.Infrastructure.Processes;

/// <summary>Boundary for starting trusted platform applications.</summary>
public interface IProcessLauncher
{
    Task<LaunchedProcess> LaunchAsync(
        ExternalLaunchSpec specification,
        CancellationToken cancellationToken = default);
}

public sealed record ExternalLaunchSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    bool UseShellExecute);

public sealed record LaunchedProcess(int? ProcessId);
