using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SmartMeter.Application;
using SmartMeter.Application.Abstractions;

namespace SmartMeter.Api.Infrastructure;

/// <summary>
/// Real-time channel: ingestion progress ("jobUpdated" to the "jobs" group) and
/// "dataVersionChanged" broadcasts so open dashboards reload after an analytics refresh.
/// </summary>
[Authorize(Policy = Policies.Viewer)]
public sealed class LiveHub : Hub
{
    public const string JobsGroup = "jobs";

    public Task WatchJobs() => Groups.AddToGroupAsync(Context.ConnectionId, JobsGroup);

    public Task UnwatchJobs() => Groups.RemoveFromGroupAsync(Context.ConnectionId, JobsGroup);
}

internal sealed class SignalRIngestionNotifier(IHubContext<LiveHub> hub) : IIngestionNotifier
{
    public Task JobUpdatedAsync(IngestionJobDto job, CancellationToken ct = default) =>
        hub.Clients.Group(LiveHub.JobsGroup).SendAsync("jobUpdated", job, ct);

    public Task DataVersionChangedAsync(long dataVersion, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync("dataVersionChanged", dataVersion, ct);
}

internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public string? Name => accessor.HttpContext?.User.Identity is { IsAuthenticated: true } id ? id.Name : null;
}
