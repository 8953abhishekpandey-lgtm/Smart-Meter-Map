using MediatR;
using Microsoft.Extensions.Caching.Hybrid;
using SmartMeter.Application.Abstractions;

namespace SmartMeter.Application.Common.Behaviors;

public sealed class CachingBehavior<TRequest, TResponse>(HybridCache cache, IAnalyticsStateProvider state)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ICacheableQuery cacheable) return await next();
        var snapshot = await state.GetAsync(cancellationToken);
        var key = $"sm:v{snapshot.DataVersion}:{typeof(TRequest).Name}:{cacheable.CacheKey}";
        var duration = cacheable.CacheDuration;
        var options = new HybridCacheEntryOptions
        {
            Expiration = duration,
            LocalCacheExpiration = duration < TimeSpan.FromMinutes(2) ? duration : TimeSpan.FromMinutes(2),
        };
        // The factory is SHARED by every concurrent caller of the same key (stampede protection), so it must run with
        // HybridCache's shared token (cancelled only when *all* callers have gone), never with the first caller's
        // request token - otherwise one user panning away would fail the identical in-flight request of everyone else.
        return await cache.GetOrCreateAsync(key, next, static async (next, shared) => await next(shared), options,
            cancellationToken: cancellationToken);
    }
}
