using Remote.Application.Connections;
using Remote.Protocols;

namespace Remote.Application.Sessions;

/// <summary>Owns active Session identity and lifecycle independently of any view.</summary>
public sealed class SessionWorkspace
{
    private readonly object _gate = new();
    private readonly List<RemoteSession> _sessions = [];

    public IReadOnlyList<RemoteSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return _sessions.ToArray();
            }
        }
    }

    public SessionOpenRequest RequestOpen(ConnectionProfile connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        RemoteSession[] existing;
        lock (_gate)
        {
            existing = _sessions
                .Where(session => session.ConnectionId == connection.Id && session.State.IsOpen())
                .ToArray();
        }

        return new(connection, existing);
    }

    public RemoteSession OpenNew(ConnectionProfile connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var session = new RemoteSession(
            SessionId.New(),
            connection.Id,
            connection.Name,
            connection.ProtocolId,
            connection.DefaultAccessMode,
            SessionState.Connecting);
        lock (_gate)
        {
            _sessions.Add(session);
        }
        return session;
    }

    public RemoteSession SetState(SessionId sessionId, SessionState state, string? failureDetail = null)
    {
        lock (_gate)
        {
            var index = _sessions.FindIndex(session => session.Id == sessionId);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
            }

            var current = _sessions[index];
            if (!current.State.CanTransitionTo(state))
            {
                throw new InvalidOperationException($"Session cannot transition from {current.State} to {state}.");
            }

            var updated = current with { State = state, FailureDetail = failureDetail };
            _sessions[index] = updated;
            return updated;
        }
    }

    public RemoteSession Get(SessionId sessionId)
    {
        lock (_gate)
        {
            return _sessions.FirstOrDefault(session => session.Id == sessionId)
                ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
        }
    }

    public bool RemoveClosed(SessionId sessionId)
    {
        lock (_gate)
        {
            var index = _sessions.FindIndex(session => session.Id == sessionId);
            if (index < 0 || _sessions[index].State.IsOpen())
            {
                return false;
            }

            _sessions.RemoveAt(index);
            return true;
        }
    }
}

public sealed record SessionOpenRequest(
    ConnectionProfile Connection,
    IReadOnlyList<RemoteSession> ExistingSessions)
{
    public bool RequiresChoice => ExistingSessions.Count > 0;
}

public sealed record RemoteSession(
    SessionId Id,
    ConnectionId ConnectionId,
    string ConnectionName,
    string ProtocolId,
    SessionAccessMode AccessMode,
    SessionState State,
    string? FailureDetail = null);

public readonly record struct SessionId(Guid Value)
{
    public static SessionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public enum SessionState
{
    Connecting,
    ExternalClientLaunched,
    Connected,
    Disconnecting,
    Disconnected,
    Faulted,
}

internal static class SessionStateExtensions
{
    public static bool IsOpen(this SessionState state) =>
        // An external client is handed off to another process and cannot be tracked
        // reliably by the workspace. It must not permanently block a retry.
        state is SessionState.Connecting or SessionState.Connected or SessionState.Disconnecting;

    public static bool CanTransitionTo(this SessionState state, SessionState next) =>
        (state, next) switch
        {
            (SessionState.Connecting, SessionState.Connected) => true,
            (SessionState.Connecting, SessionState.ExternalClientLaunched) => true,
            (SessionState.Connecting, SessionState.Faulted) => true,
            (SessionState.Connecting, SessionState.Disconnecting) => true,
            (SessionState.Connected, SessionState.Disconnecting) => true,
            (SessionState.Connected, SessionState.Faulted) => true,
            (SessionState.ExternalClientLaunched, SessionState.Disconnecting) => true,
            (SessionState.ExternalClientLaunched, SessionState.Faulted) => true,
            (SessionState.Disconnecting, SessionState.Disconnected) => true,
            _ => false,
        };
}
