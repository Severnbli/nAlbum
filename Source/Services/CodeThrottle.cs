/*
    nAlbum – telegram bot for sharing photos and videos in albums
    Copyright (C) 2026 Usevalad Buben usevaladbuben@gmail.com

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU Affero General Public License as published by
    the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
    or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

    You should have received a copy of the GNU Affero General Public License
    along with this program. If not, see <https://www.gnu.org/licenses/>.
*/

namespace nAlbum.Services;

/// <summary>
/// In-memory wrong-code throttle against guessing invite codes. Not durable and not shared across processes.
/// Per user: 5 wrong codes in 10 minutes lock code entry; every further lockout doubles, up to a day.
/// Globally: too many wrong codes from everyone together within a minute pause code entry for all users,
/// which stops guessing spread across many accounts.
/// </summary>
public sealed class CodeThrottle
{
    private const int MaxFailedCodes = 5;
    private static readonly TimeSpan FailWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FirstLockout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxLockout = TimeSpan.FromDays(1);
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(1);   // lockout level resets after a quiet day

    private const int GlobalMaxFailedCodes = 60;
    private static readonly TimeSpan GlobalWindow = TimeSpan.FromMinutes(1);

    private sealed class UserState
    {
        public readonly List<DateTime> Failures = new();
        public DateTime LockedUntil;
        public int Lockouts;
        public DateTime LastFailure;
    }

    private readonly Dictionary<long, UserState> _users = new();
    private readonly Queue<DateTime> _global = new();
    private readonly object _lock = new();

    public bool IsThrottled(long userId)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            PruneGlobal(now);
            if (_global.Count >= GlobalMaxFailedCodes) return true;
            return _users.TryGetValue(userId, out var u) && u.LockedUntil > now;
        }
    }

    public void RegisterFailedAttempt(long userId)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            _global.Enqueue(now);
            PruneGlobal(now);

            if (!_users.TryGetValue(userId, out var u)) _users[userId] = u = new UserState();
            if (now - u.LastFailure > ForgetAfter) u.Lockouts = 0;
            u.LastFailure = now;
            u.Failures.RemoveAll(t => now - t > FailWindow);
            u.Failures.Add(now);
            if (u.Failures.Count < MaxFailedCodes) return;

            var lockout = TimeSpan.FromTicks(Math.Min(FirstLockout.Ticks << Math.Min(u.Lockouts, 16), MaxLockout.Ticks));
            u.LockedUntil = now + lockout;
            u.Lockouts++;
            u.Failures.Clear();
            PruneUsers(now);
        }
    }

    private void PruneGlobal(DateTime now)
    {
        while (_global.Count > 0 && now - _global.Peek() > GlobalWindow) _global.Dequeue();
    }

    /// <summary>Drops users with nothing left to remember, so the table does not grow forever.</summary>
    private void PruneUsers(DateTime now)
    {
        foreach (var id in _users.Where(kv => kv.Value.LockedUntil <= now && now - kv.Value.LastFailure > ForgetAfter)
                     .Select(kv => kv.Key).ToList())
            _users.Remove(id);
    }
}
