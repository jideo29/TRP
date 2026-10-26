using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Wayfarer.Security;

/// <summary>
/// The Aegis JWKS is fetched once and reused. A token whose <c>kid</c> is not in the cached set triggers at
/// most one refetch per <see cref="RefreshMinInterval"/> (at least 30 seconds, 300 by default), so a stream of
/// tokens with made-up key ids cannot make Wayfarer call Aegis on every request. The set is also refetched after
/// <see cref="MaxAge"/> so a rotated key is picked up. A failed fetch is retried no sooner than
/// <see cref="FailureBackoff"/>; with no usable set every token fails closed.
/// </summary>
public sealed class AegisSigningKeyCache
{
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _fetchCount;

    public AegisSigningKeyCache()
        : this(TimeProvider.System, TimeSpan.FromSeconds(WayfarerAegisOptions.DefaultJwksRefreshMinSeconds), TimeSpan.FromMinutes(10))
    {
    }

    public AegisSigningKeyCache(TimeProvider clock, TimeSpan refreshMinInterval, TimeSpan maxAge)
    {
        _clock = clock;
        var floor = TimeSpan.FromSeconds(WayfarerAegisOptions.MinimumJwksRefreshSeconds);
        RefreshMinInterval = refreshMinInterval < floor ? floor : refreshMinInterval;
        MaxAge = maxAge <= TimeSpan.Zero ? TimeSpan.FromMinutes(10) : maxAge;
    }

    public TimeSpan RefreshMinInterval { get; }

    public TimeSpan MaxAge { get; }

    public TimeSpan FailureBackoff { get; } = TimeSpan.FromSeconds(5);

    /// <summary>Number of JWKS fetches attempted. For tests and diagnostics.</summary>
    public int FetchCount => Volatile.Read(ref _fetchCount);

    public async Task<IReadOnlyList<SecurityKey>?> GetAsync(
        HttpClient http,
        string jwksUrl,
        string? kid,
        CancellationToken cancellationToken)
    {
        var url = jwksUrl.Trim();
        if (TryCached(url, kid, out var cached))
            return cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryCached(url, kid, out cached))
                return cached;

            _entries.TryGetValue(url, out var previous);
            var now = _clock.GetUtcNow();
            Interlocked.Increment(ref _fetchCount);
            var fetched = await FetchAsync(http, url, cancellationToken).ConfigureAwait(false);
            if (fetched is null)
            {
                // Keep a still-fresh set; record the attempt so the next one waits.
                _entries[url] = previous is not null && now - previous.FetchedAt < MaxAge
                    ? previous with { LastAttempt = now }
                    : new Entry(null, DateTimeOffset.MinValue, now);
                return previous is not null && now - previous.FetchedAt < MaxAge ? previous.Keys : null;
            }

            _entries[url] = new Entry(fetched, now, now);
            return fetched;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryCached(string url, string? kid, out IReadOnlyList<SecurityKey>? keys)
    {
        keys = null;
        if (!_entries.TryGetValue(url, out var entry))
            return false;

        var now = _clock.GetUtcNow();
        if (entry.Keys is null)
        {
            // Last fetch failed: fail closed until the backoff has passed.
            return now - entry.LastAttempt < FailureBackoff;
        }

        if (now - entry.FetchedAt >= MaxAge)
            return false;

        keys = entry.Keys;
        var known = string.IsNullOrEmpty(kid) || entry.Keys.Any(k => string.Equals(k.KeyId, kid, StringComparison.Ordinal));
        if (known)
            return true;

        // Unknown kid: refetch only when the refresh interval has passed since the last attempt.
        return now - entry.LastAttempt < RefreshMinInterval;
    }

    private static async Task<IReadOnlyList<SecurityKey>?> FetchAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var keys = new JsonWebKeySet(body).GetSigningKeys().ToList();
            return keys.Count == 0 ? null : keys;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or ArgumentException
                                              or JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed record Entry(IReadOnlyList<SecurityKey>? Keys, DateTimeOffset FetchedAt, DateTimeOffset LastAttempt);
}
