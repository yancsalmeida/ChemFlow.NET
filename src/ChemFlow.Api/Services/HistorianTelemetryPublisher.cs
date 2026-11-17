using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChemFlow.Api.Contracts;
using Microsoft.Extensions.Logging;

namespace ChemFlow.Api.Services;

public sealed record HistorianPublishSnapshot(
    bool Enabled,
    string? EngineUrl,
    string? BatchCode,
    long MeasurementsPublished,
    long AlertsTriggered,
    string? LastError
);

public interface IHistorianTelemetryPublisher
{
    HistorianPublishSnapshot GetSnapshot();
    void ConfigureSession(bool enabled, string? overrideBaseUrl = null);
    Task PublishTickAsync(SupervisoryModbusTickDto tick, CancellationToken ct = default);
}

/// <summary>
/// Streams closed-loop Distributed SIL telemetry frames from ChemFlow.NET
/// into IndustrialTelemetry.Engine (PostgreSQL historian, alert evaluator, and live SSE stream).
/// Gracefully degrades without blocking the real-time Modbus control loop when unconfigured or offline.
/// </summary>
public sealed class HttpHistorianTelemetryPublisher : IHistorianTelemetryPublisher
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<HttpHistorianTelemetryPublisher> _logger;
    private readonly SemaphoreSlim _batchLock = new(1, 1);
    private readonly object _stateLock = new();

    private bool _enabled = true;
    private string? _baseUrl;
    private Guid? _activeBatchId;
    private string? _activeBatchCode;
    private long _measurementsPublished;
    private long _alertsTriggered;
    private string? _lastError;

    public HttpHistorianTelemetryPublisher(
        HttpClient http,
        ILogger<HttpHistorianTelemetryPublisher> logger)
    {
        _http = http;
        _logger = logger;
        _baseUrl = ResolveDefaultBaseUrl();
        _enabled = !string.IsNullOrWhiteSpace(_baseUrl);
    }

    private static string? ResolveDefaultBaseUrl()
    {
        string? raw = Environment.GetEnvironmentVariable("HISTORIAN_ENGINE_URL")
            ?? Environment.GetEnvironmentVariable("TELEMETRY_ENGINE_URL");
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().TrimEnd('/');
    }

    public void ConfigureSession(bool enabled, string? overrideBaseUrl = null)
    {
        string? resolved = !string.IsNullOrWhiteSpace(overrideBaseUrl)
            ? overrideBaseUrl.Trim().TrimEnd('/')
            : ResolveDefaultBaseUrl();

        lock (_stateLock)
        {
            if (!string.Equals(_baseUrl, resolved, StringComparison.OrdinalIgnoreCase))
            {
                _activeBatchId = null;
                _activeBatchCode = null;
            }

            _baseUrl = resolved;
            _enabled = enabled && !string.IsNullOrWhiteSpace(resolved);
            _lastError = null;
        }
    }

    public HistorianPublishSnapshot GetSnapshot()
    {
        lock (_stateLock)
        {
            return new HistorianPublishSnapshot(
                Enabled: _enabled,
                EngineUrl: _baseUrl,
                BatchCode: _activeBatchCode,
                MeasurementsPublished: _measurementsPublished,
                AlertsTriggered: _alertsTriggered,
                LastError: _lastError);
        }
    }

    public async Task PublishTickAsync(SupervisoryModbusTickDto tick, CancellationToken ct = default)
    {
        bool enabled;
        string? baseUrl;
        lock (_stateLock)
        {
            enabled = _enabled;
            baseUrl = _baseUrl;
        }

        if (!enabled || string.IsNullOrWhiteSpace(baseUrl))
            return;

        try
        {
            Guid? batchId = await EnsureActiveBatchAsync(baseUrl, ct).ConfigureAwait(false);
            if (!batchId.HasValue)
                return;

            string tsIso = tick.TimestampUtc.ToUniversalTime().ToString("O");
            string plantTempQuality = tick.SensorFaultDetected ? "Bad" : "Good";
            string ekfQuality = tick.SensorFaultDetected
                ? "Uncertain"
                : (tick.EkfNis > 5.99 ? "Uncertain" : "Good");

            var measurements = new List<object>(7)
            {
                new { timestampUtc = tsIso, sensorTag = "SP-TT-201", value = tick.PlantTempC, unit = "Celsius", quality = plantTempQuality },
                new { timestampUtc = tsIso, sensorTag = "SP-TT-202", value = tick.PlantJacketTempC, unit = "Celsius", quality = "Good" },
                new { timestampUtc = tsIso, sensorTag = "SP-FT-201-TFEED", value = tick.PlantFeedTempC, unit = "Celsius", quality = "Good" },
                new { timestampUtc = tsIso, sensorTag = "SP-AT-201-CA", value = tick.EkfCaMolPerL, unit = "MolPerLiter", quality = ekfQuality },
                new { timestampUtc = tsIso, sensorTag = "SP-AT-201-CB", value = tick.EkfCbMolPerL, unit = "MolPerLiter", quality = ekfQuality },
                new { timestampUtc = tsIso, sensorTag = "SP-SB-201", value = tick.PlantSelectivityBPct, unit = "Percent", quality = "Good" },
                new { timestampUtc = tsIso, sensorTag = "SP-TCV-201-NMPC", value = tick.CommandedValvePct, unit = "Percent", quality = "Good" }
            };

            var payload = new
            {
                batchId = batchId.Value,
                measurements
            };

            using var resp = await _http.PostAsJsonAsync(
                $"{baseUrl}/api/telemetry/ingest", payload, JsonOpts, ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if ((int)resp.StatusCode is 400 or 404)
                {
                    lock (_stateLock)
                    {
                        _activeBatchId = null;
                        _activeBatchCode = null;
                    }
                }
                RecordError($"Historian ingest HTTP {(int)resp.StatusCode}: {body}");
                return;
            }

            var ingestRes = await resp.Content.ReadFromJsonAsync<HistorianIngestResponse>(JsonOpts, ct).ConfigureAwait(false);
            lock (_stateLock)
            {
                _measurementsPublished += ingestRes?.MeasurementsIngested ?? measurements.Count;
                _alertsTriggered += ingestRes?.AlertsTriggered ?? 0;
                _lastError = null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RecordError(ex.Message);
            _logger.LogDebug(ex, "Historian SIL telemetry publish error to {Url}", baseUrl);
        }
    }

    private async Task<Guid?> EnsureActiveBatchAsync(string baseUrl, CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_activeBatchId.HasValue)
                return _activeBatchId;
        }

        await _batchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                if (_activeBatchId.HasValue)
                    return _activeBatchId;
            }

            using var listResp = await _http.GetAsync($"{baseUrl}/api/batches?status=InProgress&limit=1", ct).ConfigureAwait(false);
            if (listResp.IsSuccessStatusCode)
            {
                var existing = await listResp.Content.ReadFromJsonAsync<List<HistorianBatchSummary>>(JsonOpts, ct).ConfigureAwait(false);
                if (existing is { Count: > 0 })
                {
                    lock (_stateLock)
                    {
                        _activeBatchId = existing[0].Id;
                        _activeBatchCode = existing[0].Code;
                        _lastError = null;
                    }
                    return existing[0].Id;
                }
            }

            string code = $"BATCH-SIL-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            var createBody = new
            {
                code,
                productRecipe = "Series-Parallel-CSTR-Economic-NMPC",
                reactorId = "R-201-SIL",
                targetVolumeM3 = 12.0,
                operatorNotes = "Automated SIL supervisory telemetry batch initiated by ChemFlow.NET."
            };

            using var createResp = await _http.PostAsJsonAsync($"{baseUrl}/api/batches", createBody, JsonOpts, ct).ConfigureAwait(false);
            if (!createResp.IsSuccessStatusCode)
            {
                RecordError($"Failed creating historian batch HTTP {(int)createResp.StatusCode}");
                return null;
            }

            var created = await createResp.Content.ReadFromJsonAsync<HistorianBatchSummary>(JsonOpts, ct).ConfigureAwait(false);
            if (created is null)
                return null;

            using var startResp = await _http.PostAsJsonAsync($"{baseUrl}/api/batches/{created.Id}/start", new { }, JsonOpts, ct).ConfigureAwait(false);
            if (!startResp.IsSuccessStatusCode)
            {
                RecordError($"Failed starting historian batch HTTP {(int)startResp.StatusCode}");
                return null;
            }

            lock (_stateLock)
            {
                _activeBatchId = created.Id;
                _activeBatchCode = created.Code ?? code;
                _lastError = null;
            }

            return created.Id;
        }
        finally
        {
            _batchLock.Release();
        }
    }

    private void RecordError(string message)
    {
        lock (_stateLock)
        {
            _lastError = message.Length > 240 ? message[..240] : message;
        }
    }

    private sealed record HistorianBatchSummary(Guid Id, string? Code, string? Status);
    private sealed record HistorianIngestResponse(int MeasurementsIngested, int AlertsTriggered);
}
