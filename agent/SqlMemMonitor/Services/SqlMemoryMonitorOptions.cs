namespace SqlMemMonitor.Services;

public sealed class SqlMemoryMonitorOptions
{
    public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromSeconds(30);
    public bool EnableEtw { get; set; } = true;
    public string DatabasePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "sqlmem_monitor.db");
    public int RetentionDays { get; set; } = 14;
}
