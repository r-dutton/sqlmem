using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlMemDiag.Core.Collection;
using SqlMemMonitor.Data;

namespace SqlMemMonitor.Services;

public sealed class SqlMemoryMonitorService : BackgroundService
{
    private readonly SqlMemoryCollector _collector;
    private readonly ISnapshotRepository _repository;
    private readonly SqlMemoryMonitorOptions _options;
    private readonly ILogger<SqlMemoryMonitorService> _logger;

    public SqlMemoryMonitorService(
        SqlMemoryCollector collector,
        ISnapshotRepository repository,
        IOptions<SqlMemoryMonitorOptions> options,
        ILogger<SqlMemoryMonitorService> logger)
    {
        _collector = collector;
        _repository = repository;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SqlMemoryMonitorService starting with interval {Interval} and ETW={Etw}", _options.SnapshotInterval, _options.EnableEtw);

        while (!stoppingToken.IsCancellationRequested)
        {
            DateTimeOffset timestamp = DateTimeOffset.UtcNow;

            try
            {
                var report = await _collector.CaptureSnapshotAsync(stoppingToken).ConfigureAwait(false);
                await _repository.StoreSnapshotAsync(timestamp, report, stoppingToken).ConfigureAwait(false);

                DateTimeOffset retentionCutoff = timestamp - TimeSpan.FromDays(Math.Max(_options.RetentionDays, 1));
                await _repository.PurgeExpiredAsync(retentionCutoff, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to capture or persist memory snapshot");
            }

            try
            {
                await Task.Delay(_options.SnapshotInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("SqlMemoryMonitorService stopping");
    }
}
