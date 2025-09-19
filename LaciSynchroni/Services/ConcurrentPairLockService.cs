using Dalamud.Utility;
using System.Collections.Concurrent;

namespace LaciSynchroni.Services
{
    using PlayerNameHash = string;
    using ServerIndex = int;

    public class ConcurrentPairLockService
    {
        private readonly ConcurrentDictionary<PlayerNameHash, ServerIndex> _renderLocks = new(StringComparer.Ordinal);
        private readonly Lock _resourceLock = new();

        public int GetRenderLock(PlayerNameHash? playerNameHash, ServerIndex? serverIndex)
        {
            if (serverIndex is null || playerNameHash.IsNullOrWhitespace()) return -1;

            lock (_resourceLock)
            {
                return _renderLocks.GetOrAdd(playerNameHash, serverIndex.Value);
            }
        }

        public bool ReleaseRenderLock(PlayerNameHash? playerNameHash, ServerIndex? serverIndex)
        {
            if (serverIndex is null || playerNameHash.IsNullOrWhitespace()) return false;

            lock (_resourceLock)
            {
                ServerIndex existingServerIndex = _renderLocks.GetValueOrDefault(playerNameHash, -1);
                return (serverIndex == existingServerIndex) && _renderLocks.Remove(playerNameHash, out _);
            }
        }
    }
}