using Hangfire.Dashboard;

namespace SmartMeter.Api.Infrastructure;

/// <summary>Dashboard access: Development without auth, otherwise authenticated admins only (put it behind your SSO proxy).</summary>
internal sealed class HangfireDashboardAuth(bool authEnabled, bool isDevelopment) : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var http = context.GetHttpContext();
        if (!authEnabled) return isDevelopment;
        return http.User.Identity?.IsAuthenticated == true && http.User.IsInRole("admin");
    }
}
