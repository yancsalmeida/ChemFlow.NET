using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChemFlow.Api.Contracts;
using ChemFlow.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace ChemFlow.Api.Services;

public sealed record HistorianPublishSnapshot(
    bool Enabled,
    string? EngineUrl,
    string? BatchCode,
    long MeasurementsPublished,
    long AlertsTriggered,
    string? LastError,
    long QueuedMessages = 0,
    long ReplayedMessages = 0
);

public interface IHistorianTelemetryPublisher
{
    HistorianPublishSnapshot GetSnapshot();
    void ConfigureSession(bool enabled, string? overrideBaseUrl = null);
    Task PublishTickAsync(SupervisoryModbusTickDto tick, CancellationToken ct = default);
    Task<int> FlushOutboxAsync(CancellationToken ct = default);
}

/// <summary>
/// Streams closed-loop Distributed SIL telemetry frames from ChemFlow.NET
/// into IndustrialTelemetry.Engine with SQLite store-and-forward resiliency
/// and deterministic Idempotency-Key deduplication.
/// </summary>
public sealed class HttpHistorianTelemetryPublisher : IHistorianTelemetryPublisher
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<HttpHistorianTelemetryPublisher> _logger;
    private readonly SqliteHistorianOutbox? _outbox;
    private readonly SemaphoreSlim _batchLock = new(1, 1);
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly object _stateLock = new();

    private bool _enabled;
    private string? _baseUrl;
    private Guid? _activeBatchId;
    private string? _activeBatchCode;
    private long _measurementsPublished;
    private long _alertsTriggered;
    private long _queuedMessages;
    private long _replayedMessages;
    private string? _lastError;

    public HttpHistorianTelemetryPublisher(
        HttpClient http,
        ILogger<HttpHistorianTelemetryPublisher> logger,
        SqliteHistorianOutbox? outbox = null)
    {
        _http = http;
        _logger = logger;
        _outbox = outbox;
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
                LastError: _lastError,
                QueuedMessages: _queuedMessages,
                ReplayedMessages: _replayedMessages);
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

        string measurementsJson = JsonSerializer.Serialize(measurements, JsonOpts);
        string idempotencyKey = $"sil-{tick.Step}-{tick.TimestampUtc.ToUnixTimeMilliseconds()}";

        try
        {
            Guid? batchId = await EnsureActiveBatchAsync(baseUrl, ct).ConfigureAwait(false);
            if (!batchId.HasValue)
            {
                await SpoolAsync(idempotencyKey, measurementsJson, "No active Historian batch available", ct).ConfigureAwait(false);
                return;
            }

            // Replay any spooled backlog first to preserve chronological ordering
            await FlushOutboxInternalAsync(baseUrl, batchId.Value, maxEntries: 25, ct).ConfigureAwait(false);

            var (ok, ingested, alerts, err) = await SendIngestAsync(
                baseUrl, batchId.Value, measurementsJson, idempotencyKey, ct).ConfigureAwait(false);

            if (ok)
            {
                lock (_stateLock)
                {
                    _measurementsPublished += ingested > 0 ? ingested : measurements.Count;
                    _alertsTriggered += alerts;
                    _lastError = null;
                }
            }
            else
            {
                await SpoolAsync(idempotencyKey, measurementsJson, err, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await SpoolAsync(idempotencyKey, measurementsJson, ex.Message, ct).ConfigureAwait(false);
            _logger.LogDebug(ex, "Historian SIL telemetry publish error to {Url}; spooled to SQLite outbox", baseUrl);
        }
    }

    public async Task<int> FlushOutboxAsync(CancellationToken ct = default)
    {
        string? baseUrl;
        bool enabled;
        lock (_stateLock)
        {
            baseUrl = _baseUrl;
            enabled = _enabled;
        }

        if (!enabled || string.IsNullOrWhiteSpace(baseUrl) || _outbox is null)
            return 0;

        Guid? batchId = await EnsureActiveBatchAsync(baseUrl, ct).ConfigureAwait(false);
        if (!batchId.HasValue)
            return 0;

        return await FlushOutboxInternalAsync(baseUrl, batchId.Value, maxEntries: 100, ct).ConfigureAwait(false);
    }

    private async Task<int> FlushOutboxInternalAsync(string baseUrl, Guid batchId, int maxEntries, CancellationToken ct)
    {
        if (_outbox is null)
            return 0;

        if (!await _flushLock.WaitAsync(0, ct).ConfigureAwait(false))
            return 0;

        int replayed = 0;
        try
        {
            IReadOnlyList<SilOutboxEntry> pending = await _outbox.PeekOldestAsync(maxEntries, ct).ConfigureAwait(false);
            foreach (var entry in pending)
            {
                var (ok, ingested, alerts, err) = await SendIngestAsync(
                    baseUrl, batchId, entry.MeasurementsJson, entry.IdempotencyKey, ct).ConfigureAwait(false);

                if (ok)
                {
                    await _outbox.DeleteAsync(entry.Id, ct).ConfigureAwait(false);
                    replayed++;
                    lock (_stateLock)
                    {
                        _replayedMessages++;
                        _measurementsPublished += ingested;
                        _alertsTriggered += alerts;
                        _lastError = null;
                    }
                }
                else
                {
                    await _outbox.MarkFailedAsync(entry.Id, err, ct).ConfigureAwait(false);
                    break;
                }
            }

            long remaining = await _outbox.CountPendingAsync(ct).ConfigureAwait(false);
            lock (_stateLock)
            {
                _queuedMessages = remaining;
            }
        }
        finally
        {
            _flushLock.Release();
        }

        return replayed;
    }

    private async Task<(bool Ok, int Ingested, int Alerts, string? Error)> SendIngestAsync(
        string baseUrl,
        Guid batchId,
        string measurementsJson,
        string idempotencyKey,
        CancellationToken ct)
    {
        string bodyJson = $"{{\"batchId\":\"{batchId}\",\"measurements\":{measurementsJson}}}";
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/telemetry/ingest");
        req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
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
            string msg = $"Historian ingest HTTP {(int)resp.StatusCode}: {body}";
            RecordError(msg);
            return (false, 0, 0, msg);
        }

        var ingestRes = await resp.Content.ReadFromJsonAsync<HistorianIngestResponse>(JsonOpts, ct).ConfigureAwait(false);
        return (true, ingestRes?.MeasurementsIngested ?? 7, ingestRes?.AlertsTriggered ?? 0, null);
    }

    private async Task SpoolAsync(string idempotencyKey, string measurementsJson, string? error, CancellationToken ct)
    {
        RecordError(error ?? "Historian unavailable");
        if (_outbox is null)
            return;

        try
        {
            await _outbox.EnqueueAsync(idempotencyKey, measurementsJson, error, ct).ConfigureAwait(false);
            long count = await _outbox.CountPendingAsync(ct).ConfigureAwait(false);
            lock (_stateLock)
            {
                _queuedMessages = count;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed spooling SIL telemetry to SqliteHistorianOutbox");
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
