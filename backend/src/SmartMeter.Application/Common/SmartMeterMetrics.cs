using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SmartMeter.Application.Common;

/// <summary>Custom OpenTelemetry metrics/traces ("SmartMeter" meter and activity source).</summary>
public sealed class SmartMeterMetrics
{
    public const string MeterName = "SmartMeter";
    public static readonly ActivitySource ActivitySource = new(MeterName);

    private readonly Histogram<double> _requestDuration;
    private readonly Counter<long> _ingestedRows;
    private readonly Counter<long> _rejectedRows;
    private readonly Histogram<double> _refreshDuration;

    public SmartMeterMetrics(IMeterFactory factory)
    {
        var meter = factory.Create(MeterName);
        _requestDuration = meter.CreateHistogram<double>("smartmeter.request.duration", "ms", "CQRS request handling time");
        _ingestedRows = meter.CreateCounter<long>("smartmeter.ingestion.rows", "rows", "Valid rows bulk-loaded");
        _rejectedRows = meter.CreateCounter<long>("smartmeter.ingestion.rejected", "rows", "Rows rejected by validation");
        _refreshDuration = meter.CreateHistogram<double>("smartmeter.refresh.duration", "s", "Analytics refresh duration");
    }

    public void RecordRequest(string request, double ms, bool success) =>
        _requestDuration.Record(ms, new KeyValuePair<string, object?>("request", request), new KeyValuePair<string, object?>("success", success));

    public void RecordIngestion(string kind, long valid, long rejected)
    {
        _ingestedRows.Add(valid, new KeyValuePair<string, object?>("kind", kind));
        _rejectedRows.Add(rejected, new KeyValuePair<string, object?>("kind", kind));
    }

    public void RecordRefresh(double seconds) => _refreshDuration.Record(seconds);
}
