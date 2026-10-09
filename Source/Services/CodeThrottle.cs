namespace nAlbum.Services;

/// <summary>In-memory wrong-code throttle. Not durable and not shared across processes.</summary>
public sealed class CodeThrottle
{
    private const int MaxFailedCodes = 5;
    private static readonly TimeSpan FailWindow = TimeSpan.FromMinutes(10);

    private readonly Dictionary<long, List<DateTime>> _failedCodes = new();
    private readonly object _lock = new();

    public bool IsThrottled(long userId)
    {
        lock (_lock)
        {
            if (!_failedCodes.TryGetValue(userId, out var list)) return false;
            list.RemoveAll(t => DateTime.UtcNow - t > FailWindow);
            return list.Count >= MaxFailedCodes;
        }
    }

    public void RegisterFailedAttempt(long userId)
    {
        lock (_lock)
        {
            if (!_failedCodes.TryGetValue(userId, out var list))
            {
                _failedCodes[userId] = list = new List<DateTime>();
            }

            list.Add(DateTime.UtcNow);
        }
    }
}
