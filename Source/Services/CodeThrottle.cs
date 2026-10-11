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
