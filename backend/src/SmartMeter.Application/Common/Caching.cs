namespace SmartMeter.Application.Common;

/// <summary>
/// Marks a query whose result is cached (HybridCache: in-process L1 + optional Redis L2). The data
/// version of the live analytics generation is prepended to the key, so an analytics refresh
/// invalidates every entry at once without any explicit eviction.
/// </summary>
public interface ICacheableQuery
{
    string CacheKey { get; }
    TimeSpan CacheDuration => TimeSpan.FromMinutes(10);
}
