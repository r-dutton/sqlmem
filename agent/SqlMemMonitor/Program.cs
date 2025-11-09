using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlMemDiag.Core.Collection;
using SqlMemMonitor.Data;
using SqlMemMonitor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SqlMemoryMonitorOptions>(builder.Configuration.GetSection("Monitor"));

builder.Services.AddSingleton<SqlMemoryCollector>(sp =>
{
    var options = sp.GetRequiredService<IOptions<SqlMemoryMonitorOptions>>().Value;
    return new SqlMemoryCollector(new SqlMemoryCollectorOptions { EnableEtw = options.EnableEtw });
});

builder.Services.AddSingleton<ISnapshotRepository, SqliteSnapshotRepository>();
builder.Services.AddHostedService<SqlMemoryMonitorService>();

var app = builder.Build();

app.MapGet("/api/timeseries/sql", async (ISnapshotRepository repo, int? hours, CancellationToken token) =>
{
    int windowHours = hours is > 0 and <= 168 ? hours.Value : 24;
    return Results.Json(await repo.GetSqlSeriesAsync(TimeSpan.FromHours(windowHours), token).ConfigureAwait(false));
});

app.MapGet("/api/snapshots/latest", async (ISnapshotRepository repo, CancellationToken token) =>
    Results.Json(await repo.GetLatestSnapshotAsync(token).ConfigureAwait(false)));

app.MapGet("/api/findings", async (ISnapshotRepository repo, CancellationToken token) =>
    Results.Json(await repo.GetRecentFindingsAsync(20, token).ConfigureAwait(false)));

app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();

public partial class Program { }
