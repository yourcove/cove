using System.Collections.Concurrent;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace Cove.Data.Services;

public sealed class SegmentSpanCacheRegistry(IMemoryCache memoryCache) : ISegmentSpanCacheInvalidator
{
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> _videoCacheKeys = new();
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> _profileCacheKeys = new();
    private readonly ConcurrentDictionary<string, RegistrationToken> _registrations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, TokenState> _videoChangeTokens = new();
    private readonly ConcurrentDictionary<int, TokenState> _profileChangeTokens = new();
    private CancellationTokenSource _allChangeToken = new();

    public int RegistrationCount => _registrations.Count;
    internal int VideoTokenCount => _videoChangeTokens.Count;
    internal int ProfileTokenCount => _profileChangeTokens.Count;

    public ChangeTokenLease AcquireVideoChangeToken(int videoId) =>
        AcquireChangeToken(_videoChangeTokens, _videoCacheKeys, videoId);

    public ChangeTokenLease AcquireProfileChangeToken(int profileId) =>
        AcquireChangeToken(_profileChangeTokens, _profileCacheKeys, profileId);

    public IChangeToken GetAllChangeToken() =>
        new CancellationChangeToken(Volatile.Read(ref _allChangeToken).Token);

    public RegistrationToken Register(int videoId, int profileId, string cacheKey)
    {
        var registration = new RegistrationToken(videoId, profileId);
        _registrations[cacheKey] = registration;
        _videoCacheKeys.GetOrAdd(videoId, static _ => new ConcurrentDictionary<string, byte>())[cacheKey] = 0;
        _profileCacheKeys.GetOrAdd(profileId, static _ => new ConcurrentDictionary<string, byte>())[cacheKey] = 0;
        return registration;
    }

    public RegistrationToken RegisterVideo(int videoId, string cacheKey)
    {
        var registration = new RegistrationToken(videoId, null);
        _registrations[cacheKey] = registration;
        _videoCacheKeys.GetOrAdd(videoId, static _ => new ConcurrentDictionary<string, byte>())[cacheKey] = 0;
        return registration;
    }

    public RegistrationToken RegisterProfile(int profileId, string cacheKey)
    {
        var registration = new RegistrationToken(null, profileId);
        _registrations[cacheKey] = registration;
        _profileCacheKeys.GetOrAdd(profileId, static _ => new ConcurrentDictionary<string, byte>())[cacheKey] = 0;
        return registration;
    }

    public void Unregister(string cacheKey)
    {
        if (!_registrations.TryRemove(cacheKey, out var registration))
            return;

        if (registration.VideoId is int videoId)
        {
            RemoveKey(_videoCacheKeys, videoId, cacheKey);
            TryPruneChangeToken(_videoChangeTokens, _videoCacheKeys, videoId);
        }
        if (registration.ProfileId is int profileId)
        {
            RemoveKey(_profileCacheKeys, profileId, cacheKey);
            TryPruneChangeToken(_profileChangeTokens, _profileCacheKeys, profileId);
        }
    }

    public void Unregister(string cacheKey, RegistrationToken expected)
    {
        if (!_registrations.TryRemove(new KeyValuePair<string, RegistrationToken>(cacheKey, expected)))
            return;

        if (expected.VideoId is int videoId)
        {
            RemoveKey(_videoCacheKeys, videoId, cacheKey);
            TryPruneChangeToken(_videoChangeTokens, _videoCacheKeys, videoId);
        }
        if (expected.ProfileId is int profileId)
        {
            RemoveKey(_profileCacheKeys, profileId, cacheKey);
            TryPruneChangeToken(_profileChangeTokens, _profileCacheKeys, profileId);
        }
    }

    public void InvalidateVideo(int videoId)
    {
        CancelAndRemove(_videoChangeTokens, videoId);
        if (!_videoCacheKeys.TryRemove(videoId, out var keys))
            return;

        foreach (var key in keys.Keys)
        {
            Unregister(key);
            memoryCache.Remove(key);
        }
    }

    public void InvalidateProfile(int profileId)
    {
        CancelAndRemove(_profileChangeTokens, profileId);
        if (_profileCacheKeys.TryRemove(profileId, out var keys))
        {
            foreach (var key in keys.Keys)
            {
                Unregister(key);
                memoryCache.Remove(key);
            }
        }

        memoryCache.Remove($"segment-display-rules:{profileId}");
    }

    public void InvalidateAll()
    {
        var previous = Interlocked.Exchange(ref _allChangeToken, new CancellationTokenSource());
        previous.Cancel();

        foreach (var cacheKey in _registrations.Keys)
        {
            Unregister(cacheKey);
            memoryCache.Remove(cacheKey);
        }
    }

    private static void RemoveKey(
        ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> buckets,
        int bucketId,
        string cacheKey)
    {
        if (!buckets.TryGetValue(bucketId, out var keys))
            return;
        keys.TryRemove(cacheKey, out _);
        if (keys.IsEmpty)
            buckets.TryRemove(new KeyValuePair<int, ConcurrentDictionary<string, byte>>(bucketId, keys));
    }

    private static ChangeTokenLease AcquireChangeToken(
        ConcurrentDictionary<int, TokenState> tokens,
        ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> cacheKeys,
        int id)
    {
        while (true)
        {
            var state = tokens.GetOrAdd(id, static _ => new TokenState());
            lock (state.Gate)
            {
                if (state.Retired)
                    continue;

                state.ActiveLeases++;
                return new ChangeTokenLease(
                    new CancellationChangeToken(state.Source.Token),
                    () =>
                    {
                        lock (state.Gate)
                            state.ActiveLeases--;
                        TryPruneChangeToken(tokens, cacheKeys, id);
                    });
            }
        }
    }

    private static void CancelAndRemove(
        ConcurrentDictionary<int, TokenState> tokens,
        int id)
    {
        if (!tokens.TryRemove(id, out var state))
            return;

        lock (state.Gate)
            state.Retired = true;
        state.Source.Cancel();
    }

    private static void TryPruneChangeToken(
        ConcurrentDictionary<int, TokenState> tokens,
        ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> cacheKeys,
        int id)
    {
        if (cacheKeys.ContainsKey(id) || !tokens.TryGetValue(id, out var state))
            return;

        lock (state.Gate)
        {
            if (state.Retired || state.ActiveLeases != 0 || cacheKeys.ContainsKey(id))
                return;
            state.Retired = true;
            tokens.TryRemove(new KeyValuePair<int, TokenState>(id, state));
        }
    }

    private sealed class TokenState
    {
        public object Gate { get; } = new();
        public CancellationTokenSource Source { get; } = new();
        public int ActiveLeases { get; set; }
        public bool Retired { get; set; }
    }

    public sealed class ChangeTokenLease(IChangeToken token, Action release) : IDisposable
    {
        private int _disposed;

        public IChangeToken Token { get; } = token;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                release();
        }
    }

    public sealed class RegistrationToken(int? videoId, int? profileId)
    {
        public int? VideoId { get; } = videoId;
        public int? ProfileId { get; } = profileId;
    }
}
