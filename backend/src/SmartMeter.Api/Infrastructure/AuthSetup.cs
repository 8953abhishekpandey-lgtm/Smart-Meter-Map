using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace SmartMeter.Api.Infrastructure;

public static class Policies
{
    /// <summary>Read access to dashboards, map and analytics.</summary>
    public const string Viewer = "Viewer";
    /// <summary>Uploads and analytics refresh (roles: ingestion-operator, admin).</summary>
    public const string Operator = "Operator";
}

public sealed class AuthOptions
{
    public const string Section = "Auth";
    /// <summary>Dev toggle: false = every endpoint is anonymous (local development / demos only).</summary>
    public bool Enabled { get; set; }
    /// <summary>"Oidc" (Entra ID, Keycloak, Okta ... via Authority) or "Dev" (locally signed tokens).</summary>
    public string Mode { get; set; } = "Oidc";
    public string? Authority { get; set; }
    public string? Audience { get; set; }
    /// <summary>Public SPA client id / scope advertised to the Angular app for the OIDC code flow.</summary>
    public string? ClientId { get; set; }
    public string? Scope { get; set; }
    public bool RequireHttpsMetadata { get; set; } = true;
    public string RoleClaimType { get; set; } = "roles";
    public string NameClaimType { get; set; } = "preferred_username";
    public string DevIssuer { get; set; } = "smartmeter-dev";
    public string DevAudience { get; set; } = "smartmeter-api";
    /// <summary>HMAC key for Dev mode (&gt;= 32 chars). Supply via environment (Auth__DevSigningKey).</summary>
    public string? DevSigningKey { get; set; }

    public bool IsDev => string.Equals(Mode, "Dev", StringComparison.OrdinalIgnoreCase);

    public SymmetricSecurityKey DevKey()
    {
        if (string.IsNullOrEmpty(DevSigningKey) || DevSigningKey.Length < 32)
            throw new InvalidOperationException("Auth:DevSigningKey must be at least 32 characters when Auth:Mode = Dev.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(DevSigningKey));
    }
}

public static class AuthSetup
{
    public static IServiceCollection AddSmartMeterAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var o = configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.Section));

        if (!o.Enabled)
        {
            services.AddAuthentication();
            services.AddAuthorizationBuilder()
                .AddPolicy(Policies.Viewer, p => p.RequireAssertion(_ => true))
                .AddPolicy(Policies.Operator, p => p.RequireAssertion(_ => true));
            return services;
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
        {
            if (o.IsDev)
            {
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = o.DevIssuer,
                    ValidAudience = o.DevAudience,
                    IssuerSigningKey = o.DevKey(),
                    RoleClaimType = ClaimTypes.Role,
                    NameClaimType = ClaimTypes.Name,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
            }
            else
            {
                jwt.Authority = o.Authority ?? throw new InvalidOperationException("Auth:Authority is required when Auth:Mode = Oidc.");
                jwt.Audience = o.Audience;
                jwt.RequireHttpsMetadata = o.RequireHttpsMetadata;
                jwt.TokenValidationParameters.RoleClaimType = o.RoleClaimType;
                jwt.TokenValidationParameters.NameClaimType = o.NameClaimType;
            }
            jwt.MapInboundClaims = false;
            jwt.Events = new JwtBearerEvents
            {
                // Browsers cannot set headers on WebSocket upgrades: SignalR sends the token in the query string.
                OnMessageReceived = ctx =>
                {
                    var token = ctx.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                        ctx.Token = token;
                    return Task.CompletedTask;
                },
            };
        });

        var authenticated = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(authenticated)
            .AddPolicy(Policies.Viewer, authenticated)
            .AddPolicy(Policies.Operator, p => p.RequireAuthenticatedUser().RequireRole("ingestion-operator", "admin"));
        return services;
    }
}
