using MeterMap.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DatabaseOptions>(
    builder.Configuration.GetSection("DatabaseOptions"));

builder.Services.AddSingleton<SqlConnectionFactory>();
builder.Services.AddSingleton<MeterRepository>();
builder.Services.AddSingleton<MeterStatusService>();   // logic

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/ping", () => new
{
    ok = true,
    time = DateTime.Now
});

// GPS-only endpoint
app.MapGet("/api/gps-meters", async (
    MeterRepository repo,
    CancellationToken ct) =>
{
    try
    {
        var meters = await repo.GetMappingAsync(ct);
        return Results.Ok(new { count = meters.Count, meters });
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(499);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "GPS meter loading failed");
        return Results.Problem(
            title: "GPS meter loading failed",
            detail: ex.Message,
            statusCode: 500);
    }
});

// Endpoint used by wwwroot/index.html

app.MapGet("/api/meter-status", async (
    MeterStatusService svc,
    DateTime? from,
    DateTime? to,
    int? days,
    bool? today,
    int? stale,
    CancellationToken ct) =>
{
    try
    {
        var staleHours = Math.Clamp(stale ?? 24, 1, 24 * 365);
        var useToday = today ?? true;
        var daysClamped = Math.Clamp(days ?? 7, 1, 400);

        // Validation (purane Program.cs jaisi)
        if (from.HasValue && to.HasValue)
        {
            if (from.Value.Date > to.Value.Date)
                return Results.BadRequest(new { error = "'from' must be earlier than 'to'." });

            if ((to.Value.Date.AddDays(1) - from.Value.Date).TotalDays > 366)
                return Results.BadRequest(new { error = "Date range too large (max 366 days)." });
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await svc.GetAsync(from, to, daysClamped, useToday, staleHours, ct);
        app.Logger.LogInformation("meter-status took {Ms} ms ({Count} meters)",
            sw.ElapsedMilliseconds, result.Total);

      
        return Results.Ok(new
        {
            refTime = result.RefTime,
            statusRef = result.StatusRef,
            dataMax = result.DataMax,
            from = from ?? result.RefTime.AddDays(-result.Days),
            to = to ?? result.RefTime,
            warnings = result.Warnings ?? new List<string>(),

          
            total = result.Total,
            communicating = result.Communicating,
            buildings = result.Buildings,
            areas = result.Areas,

            meters = result.Meters
        });
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(499);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Meter status failed");
        return Results.Problem(
            title: "Meter status failed",
            detail: ex.Message,
            statusCode: 500);
    }
});

app.Run();