using System.IO.Compression;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Hangfire;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using OpenTelemetry;
using Microsoft.AspNetCore.ResponseCompression;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;
using SmartMeter.Api.Infrastructure;
using SmartMeter.Application;
using SmartMeter.Application.Abstractions;
using SmartMeter.Application.Common;
using SmartMeter.Infrastructure;
using SmartMeter.Infrastructure.Jobs;
using SmartMeter.Infrastructure.Persistence;
using StackExchange.Redis;

Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    var config = builder.Configuration;

    // ------------------------------------------------------------------ logging (Serilog, structured)
    builder.Services.AddSerilog((sp, lc) => lc
        .ReadFrom.Configuration(config)
        .ReadFrom.Services(sp)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "SmartMeter.Api"));

    // ------------------------------------------------------------------ layers
    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(config);
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddSingleton<ICurrentUser, HttpCurrentUser>();
    builder.Services.AddSingleton<IIngestionNotifier, SignalRIngestionNotifier>();

    // ------------------------------------------------------------------ web
    builder.Services.AddControllers().AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddOpenApi();
    builder.Services.AddSmartMeterAuth(config);

    var redis = config.GetConnectionString("Redis");
    var signalR = builder.Services.AddSignalR(o => o.EnableDetailedErrors = builder.Environment.IsDevelopment())
        .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));
    if (!string.IsNullOrWhiteSpace(redis))
        signalR.AddStackExchangeRedis(redis, o => o.Configuration.ChannelPrefix = RedisChannel.Literal("smartmeter"));

    builder.Services.AddResponseCompression(o =>
    {
        o.EnableForHttps = true;
        o.Providers.Add<BrotliCompressionProvider>();
        o.Providers.Add<GzipCompressionProvider>();
        o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/json", "application/problem+json", "text/csv"]);
    });
    builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
    builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        // heavy spatial analytics share a bounded concurrency budget per instance
        o.AddConcurrencyLimiter("analytics", l =>
        {
            l.PermitLimit = config.GetValue("RateLimiting:AnalyticsConcurrency", 8);
            l.QueueLimit = 32;
            l.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        });
        o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = config.GetValue("RateLimiting:RequestsPerMinute", 3000),
                    Window = TimeSpan.FromMinutes(1),
                }));
    });

    var corsOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    {
        if (corsOrigins.Length > 0) p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    }));

    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        if (config.GetValue("ReverseProxy:TrustAllProxies", false))
        {
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
        }
    });

    // ------------------------------------------------------------------ health checks
    var health = builder.Services.AddHealthChecks()
        .AddSqlServer(sp => sp.GetRequiredService<ISqlConnectionFactory>().ConnectionString, name: "sqlserver", tags: ["ready"], timeout: TimeSpan.FromSeconds(5))
        .AddCheck<AnalyticsFreshnessHealthCheck>("analytics-freshness", tags: ["ready"]);
    if (!string.IsNullOrWhiteSpace(redis)) health.AddRedis(redis, name: "redis", tags: ["ready"], timeout: TimeSpan.FromSeconds(3));

    // ------------------------------------------------------------------ OpenTelemetry (traces + metrics; OTLP when configured)
    var otel = builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("smartmeter-api", serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
        .WithTracing(t => t
            .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
            .AddHttpClientInstrumentation()
            .AddSqlClientInstrumentation()
            .AddSource(SmartMeterMetrics.MeterName))
        .WithMetrics(m => m
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter(SmartMeterMetrics.MeterName));
    if (!string.IsNullOrWhiteSpace(config["OTEL_EXPORTER_OTLP_ENDPOINT"])) otel.UseOtlpExporter();

    builder.WebHost.ConfigureKestrel(k =>
    {
        k.AddServerHeader = false;
        k.Limits.MaxRequestBodySize = 64L * 1024 * 1024;
    });

    // ================================================================== pipeline
    var app = builder.Build();
    var authEnabled = config.GetValue("Auth:Enabled", false);
    if (!authEnabled) app.Logger.LogWarning("Authentication is DISABLED (Auth:Enabled = false). Do not run like this in production.");

    app.UseForwardedHeaders();
    app.UseExceptionHandler();
    app.UseStatusCodePages();
    app.UseResponseCompression();
    app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) =>
        ex is not null || ctx.Response.StatusCode >= 500 ? LogEventLevel.Error
        : ctx.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Verbose
        : LogEventLevel.Information);
    app.Use(async (ctx, next) =>
    {
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers.XFrameOptions = "DENY";
        await next();
    });
    app.UseCors();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();

    app.MapControllers();
    app.MapHub<LiveHub>("/hubs/live");
    app.MapHealthChecks("/health/live", HealthResponse.Options(_ => false)).AllowAnonymous().DisableRateLimiting();
    app.MapHealthChecks("/health/ready", HealthResponse.Options(r => r.Tags.Contains("ready"))).AllowAnonymous().DisableRateLimiting();
    app.MapHealthChecks("/health", HealthResponse.Options()).AllowAnonymous().DisableRateLimiting();

    if (config.GetValue("Api:EnableDocs", app.Environment.IsDevelopment()))
    {
        app.MapOpenApi().AllowAnonymous();
        app.MapScalarApiReference(o => o.WithTitle("Smart Meter GIS Platform API")).AllowAnonymous();
    }
    if (config.GetValue("Jobs:DashboardEnabled", true))
        app.MapHangfireDashboard("/hangfire", new DashboardOptions
        {
            Authorization = [new HangfireDashboardAuth(authEnabled, app.Environment.IsDevelopment())],
            DisplayStorageConnectionString = false,
            DashboardTitle = "Smart Meter jobs",
        }).AllowAnonymous();

    app.MapGet("/", () => Results.Redirect(app.Environment.IsDevelopment() ? "/scalar" : "/health")).ExcludeFromDescription().AllowAnonymous();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "SmartMeter.Api terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

public partial class Program;
