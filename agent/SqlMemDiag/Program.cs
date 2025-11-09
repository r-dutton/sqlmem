using SqlMemDiag.Core.Collection;
using SqlMemDiag.Core.Reporting;

namespace SqlMemDiag;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        bool enableEtw = !args.Contains("--no-etw", StringComparer.OrdinalIgnoreCase);
        bool json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);

        using var collector = new SqlMemoryCollector(new SqlMemoryCollectorOptions { EnableEtw = enableEtw });
        SqlMemoryReport report = await collector.CaptureSnapshotAsync(CancellationToken.None).ConfigureAwait(false);

        if (json)
        {
            Console.WriteLine(report.ToJson());
        }
        else
        {
            ReportPrinter.Print(report);
        }

        return 0;
    }
}
