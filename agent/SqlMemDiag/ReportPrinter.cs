using SqlMemDiag.Core.Reporting;

namespace SqlMemDiag;

internal static class ReportPrinter
{
    public static void Print(SqlMemoryReport report)
    {
        Console.WriteLine($"Total Physical: {report.Summary.TotalPhysicalGiB:F1} GiB");
        Console.WriteLine($"Available     : {report.Summary.AvailablePhysicalGiB:F1} GiB");
        Console.WriteLine($"Kernel NP/P   : {report.Summary.KernelNonPagedGiB:F1} / {report.Summary.KernelPagedGiB:F1} GiB");
        Console.WriteLine($"System Cache  : {report.Summary.SystemCacheGiB:F1} GiB");
        Console.WriteLine();
        Console.WriteLine("Top processes:");

        foreach (var process in report.Processes
                     .OrderByDescending(p => p.PrivateBytes)
                     .Take(10))
        {
            Console.WriteLine(
                $" - {process.ImageName} (PID {process.Pid}) WS={process.WorkingSetGiB:F1} GiB Private={process.PrivateGiB:F1} GiB Hidden={process.PrivateMinusWorkingSetGiB:F1} GiB");
        }

        Console.WriteLine();
        Console.WriteLine("Findings:");
        if (report.Findings.Count == 0)
        {
            Console.WriteLine(" - No dominant culprit detected. Inspect driver/pool consumers.");
        }
        else
        {
            foreach (var finding in report.Findings.OrderByDescending(f => f.SeverityScore))
            {
                Console.WriteLine($" - [{finding.Id}] {finding.Title}: {finding.Description}");
            }
        }
    }
}
