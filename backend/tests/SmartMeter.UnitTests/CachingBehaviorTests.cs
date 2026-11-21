using MediatR;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;
using SmartMeter.Application.Common.Behaviors;

namespace SmartMeter.UnitTests;

public class CachingBehaviorTests
{
    private sealed record Probe(string CacheKey) : IRequest<int>, ICacheableQuery;

    private sealed class State : IAnalyticsStateProvider
    {
        public ValueTask<AnalyticsSnapshot> GetAsync(CancellationToken ct) => ValueTask.FromResult(Fixtures.Snapshot());
        public void Invalidate() { }
    }

    private static CachingBehavior<Probe, int> Create()
    {
        var sp = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider();
        return new CachingBehavior<Probe, int>(sp.GetRequiredService<HybridCache>(), new State());
    }

    [Fact]
    public async Task A_cancelled_caller_does_not_fail_other_callers_of_the_same_in_flight_query()
    {
        var behavior = Create();
        var started = new TaskCompletionSource();
        var executions = 0;
        // Mirrors MediatR: next() without a token falls back to the *caller's* request token.
        RequestHandlerDelegate<int> HandlerFor(CancellationToken caller) => async t =>
        {
            Interlocked.Increment(ref executions);
            started.TrySetResult();
            await Task.Delay(300, t == default ? caller : t);   // the DB query honours the token it is handed
            return 42;
        };

        using var first = new CancellationTokenSource();
        var a = behavior.Handle(new Probe("k"), HandlerFor(first.Token), first.Token);
        await started.Task;
        var b = behavior.Handle(new Probe("k"), HandlerFor(CancellationToken.None), CancellationToken.None);  // joins the stampede
        first.Cancel();                                                                                       // user 1 pans away

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        Assert.Equal(42, await b);
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task Results_are_cached_per_data_version_and_key()
    {
        var behavior = Create();
        var executions = 0;
        RequestHandlerDelegate<int> handler = _ => Task.FromResult(Interlocked.Increment(ref executions));
        Assert.Equal(1, await behavior.Handle(new Probe("x"), handler, CancellationToken.None));
        Assert.Equal(1, await behavior.Handle(new Probe("x"), handler, CancellationToken.None));
        Assert.Equal(2, await behavior.Handle(new Probe("y"), handler, CancellationToken.None));
    }
}
