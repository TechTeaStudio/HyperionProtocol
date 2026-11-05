namespace TechTeaStudio.Protocols.Hyperion;

/// <summary>
/// Couples the caller's <see cref="CancellationToken"/> with an optional idle timer.
/// The timer is armed right before an awaited I/O call and disarmed right after it, so a timeout measures
/// time spent waiting on the peer, not time spent in user code between calls (for example a streaming
/// consumer writing a chunk to disk).
/// </summary>
internal readonly struct OperationGuard : IDisposable
{
    private readonly CancellationTokenSource? _cts;
    private readonly CancellationToken _userToken;
    private readonly TimeSpan _timeout;

    private OperationGuard(CancellationTokenSource? cts, CancellationToken userToken, TimeSpan timeout)
    {
        _cts = cts;
        _userToken = userToken;
        _timeout = timeout;
    }

    /// <summary>Creates a guard. When <paramref name="timeout"/> is null the guard is a pass-through for <paramref name="userToken"/>.</summary>
    public static OperationGuard Create(TimeSpan? timeout, CancellationToken userToken)
    {
        if (timeout is null)
            return new OperationGuard(null, userToken, System.Threading.Timeout.InfiniteTimeSpan);

        return new OperationGuard(CancellationTokenSource.CreateLinkedTokenSource(userToken), userToken, timeout.Value);
    }

    /// <summary>Token to pass to every I/O call made under this guard.</summary>
    public CancellationToken Token => _cts?.Token ?? _userToken;

    /// <summary>The configured idle timeout, or null when disabled.</summary>
    public TimeSpan? IdleTimeout => _cts is null ? null : _timeout;

    /// <summary>True when the idle timer fired and the caller's own token was not cancelled.</summary>
    public bool TimedOut => _cts is not null && _cts.IsCancellationRequested && !_userToken.IsCancellationRequested;

    /// <summary>(Re)starts the idle timer. Call immediately before an awaited I/O operation.</summary>
    public void Arm() => _cts?.CancelAfter(_timeout);

    /// <summary>Stops the idle timer. Call right after the awaited I/O operation completed.</summary>
    public void Disarm() => _cts?.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);

    /// <inheritdoc />
    public void Dispose() => _cts?.Dispose();
}
