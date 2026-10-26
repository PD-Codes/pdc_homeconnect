namespace PdcHomeConnect.Core.Util;

/// <summary>Auto-reset signal that can be awaited. Multiple Set calls collapse into one wake-up.</summary>
public sealed class AsyncSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Set()
    {
        lock (_semaphore)
        {
            if (_semaphore.CurrentCount == 0)
            {
                _semaphore.Release();
            }
        }
    }

    public Task WaitAsync(CancellationToken cancellationToken) => _semaphore.WaitAsync(cancellationToken);

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _semaphore.WaitAsync(timeout, cancellationToken);
}
