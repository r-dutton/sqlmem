using SqlMemDiag.Core.Models;
using SqlMemDiag.Core.Reporting;

namespace SqlMemDiag.Core.Analysis;

public sealed class SqlMemoryAnalyzer
{
    private const double HiddenMemoryGapGiBThreshold = 2.0;
    private const double SqlLockedMemoryThresholdFraction = 0.2;
    private const double SqlCommitMinusWorkingSetGiBThreshold = 8.0;
    private const double VmmemDominanceFraction = 0.3;

    public SqlMemoryReport Analyze(SqlMemSummary summary, IReadOnlyDictionary<uint, EtwProcessMemoryStats> etwStats)
    {
        IReadOnlyList<SqlMemProcessEntry> processes = summary.Processes;
        List<DiagnosticFinding> findings = new();

        double totalPhysicalInUseGiB = summary.TotalPhysicalGiB - summary.AvailablePhysicalGiB;
        double totalWorkingSetGiB = processes.Sum(p => p.WorkingSetGiB);
        double hiddenGapGiB = totalPhysicalInUseGiB - totalWorkingSetGiB;
        if (hiddenGapGiB < 0)
        {
            hiddenGapGiB = 0;
        }

        SqlMemProcessEntry? sql = processes.FirstOrDefault(p => p.IsSqlServer);
        SqlMemProcessEntry? vmmem = processes.FirstOrDefault(p => p.IsVmmemOrVm);

        if (sql is not null)
        {
            double lockedEstimateGiB = sql.LockedGiB + sql.LargePageGiB;
            if (etwStats.TryGetValue(sql.Pid, out var sqlEtw))
            {
                lockedEstimateGiB = Math.Max(lockedEstimateGiB, BytesToGiB(sqlEtw.LockedBytesEstimate + sqlEtw.LargePageBytesEstimate));
            }

            if (lockedEstimateGiB >= summary.TotalPhysicalGiB * SqlLockedMemoryThresholdFraction)
            {
                findings.Add(new DiagnosticFinding(
                    "SQL-LPIM",
                    "SQL Server locked or large-page memory",
                    $"sqlservr.exe PID {sql.Pid} is estimated to hold {lockedEstimateGiB:F1} GiB in locked or large pages.",
                    1.0));
            }

            if (sql.PrivateMinusWorkingSetGiB >= SqlCommitMinusWorkingSetGiBThreshold)
            {
                findings.Add(new DiagnosticFinding(
                    "SQL-COMMIT",
                    "SQL Server private commit greatly exceeds working set",
                    $"sqlservr.exe PID {sql.Pid} has {sql.PrivateMinusWorkingSetGiB:F1} GiB of private commit beyond its working set, indicating hidden locked memory or large pages.",
                    0.7));
            }
        }

        if (vmmem is not null)
        {
            if (vmmem.PrivateGiB >= summary.TotalPhysicalGiB * VmmemDominanceFraction)
            {
                findings.Add(new DiagnosticFinding(
                    "WSL2",
                    "WSL2/Hyper-V memory pressure",
                    $"vmmem PID {vmmem.Pid} is consuming {vmmem.PrivateGiB:F1} GiB, a dominant share of physical memory.",
                    0.9));
            }
        }

        if (hiddenGapGiB >= HiddenMemoryGapGiBThreshold && findings.Count == 0)
        {
            findings.Add(new DiagnosticFinding(
                "GAP",
                "Large gap between physical usage and working sets",
                $"Approximately {hiddenGapGiB:F1} GiB of physical memory is unaccounted for by working sets. Inspect kernel pools or driver allocations.",
                0.5));
        }

        return new SqlMemoryReport(summary, processes, findings, etwStats);
    }

    private static double BytesToGiB(long value) => value / 1024d / 1024d / 1024d;
}
