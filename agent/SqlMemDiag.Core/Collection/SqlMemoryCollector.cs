using SqlMemDiag.Core.Analysis;
using SqlMemDiag.Core.Driver;
using SqlMemDiag.Core.Etw;
using SqlMemDiag.Core.Models;
using SqlMemDiag.Core.Reporting;

namespace SqlMemDiag.Core.Collection;

public sealed class SqlMemoryCollector : IDisposable
{
    private readonly bool _enableEtw;
    private readonly DriverClient _client;
    private EtwMemoryTracker? _etw;
    private readonly SqlMemoryAnalyzer _analyzer = new();
    private bool _initialized;

    public SqlMemoryCollector(SqlMemoryCollectorOptions? options = null)
    {
        options ??= new SqlMemoryCollectorOptions();
        _enableEtw = options.EnableEtw;
        _client = new DriverClient();
    }

    public async Task<SqlMemoryReport> CaptureSnapshotAsync(CancellationToken cancellationToken)
    {
        EnsureEtwStarted();
        if (_etw is not null)
        {
            // Allow ETW some time to accumulate events before snapshotting.
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        SqlMemSummary summary = _client.GetSummary();
        IReadOnlyDictionary<uint, EtwProcessMemoryStats> etwStats = _etw?.BuildSnapshot() ?? new Dictionary<uint, EtwProcessMemoryStats>();
        return _analyzer.Analyze(summary, etwStats);
    }

    private void EnsureEtwStarted()
    {
        if (_initialized)
        {
            return;
        }

        if (_enableEtw)
        {
            try
            {
                _etw = new EtwMemoryTracker();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[warn] Failed to initialise ETW session: {ex.Message}");
                _etw?.Dispose();
                _etw = null;
            }
        }

        _initialized = true;
    }

    public void Dispose()
    {
        _etw?.Dispose();
        _client.Dispose();
    }
}

public sealed class SqlMemoryCollectorOptions
{
    public bool EnableEtw { get; init; } = true;
}
