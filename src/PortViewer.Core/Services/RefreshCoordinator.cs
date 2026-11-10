namespace PortViewer.Core.Services;

public sealed class RefreshCoordinator
{
    private int _isRefreshing;

    public bool TryEnter()
    {
        return Interlocked.CompareExchange(ref _isRefreshing, 1, 0) == 0;
    }

    public void Exit()
    {
        Volatile.Write(ref _isRefreshing, 0);
    }
}
