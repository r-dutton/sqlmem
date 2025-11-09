using System.Text.Json;
using SqlMemDiag.Core.Analysis;
using SqlMemDiag.Core.Models;

namespace SqlMemDiag.Core.Reporting;

public sealed record SqlMemoryReport(
    SqlMemSummary Summary,
    IReadOnlyList<SqlMemProcessEntry> Processes,
    IReadOnlyList<DiagnosticFinding> Findings,
    IReadOnlyDictionary<uint, EtwProcessMemoryStats> EtwStats)
{
    public string ToJson()
    {
        var payload = new
        {
            summary = new
            {
                totalPhysicalGiB = Summary.TotalPhysicalGiB,
                availablePhysicalGiB = Summary.AvailablePhysicalGiB,
                kernelNonPagedGiB = Summary.KernelNonPagedGiB,
                kernelPagedGiB = Summary.KernelPagedGiB,
                systemCacheGiB = Summary.SystemCacheGiB,
                usesForensicPfns = Summary.UsesForensicPfns
            },
            processes = Processes.Select(p => new
            {
                pid = p.Pid,
                imageName = p.ImageName,
                workingSetGiB = p.WorkingSetGiB,
                privateGiB = p.PrivateGiB,
                hiddenGiB = p.PrivateMinusWorkingSetGiB,
                lockedGiB = p.LockedGiB,
                largePageGiB = p.LargePageGiB,
                isSqlServer = p.IsSqlServer,
                isVmmemOrVm = p.IsVmmemOrVm,
                hasLockPagesPrivilege = p.HasLockPagesPrivilege
            }),
            findings = Findings.Select(f => new
            {
                id = f.Id,
                title = f.Title,
                description = f.Description,
                severity = f.SeverityScore
            }),
            etw = EtwStats.ToDictionary(
                kvp => kvp.Key,
                kvp => new
                {
                    lockedBytesEstimate = kvp.Value.LockedBytesEstimate,
                    largePageBytesEstimate = kvp.Value.LargePageBytesEstimate,
                    commitDeltaBytes = kvp.Value.CommitDeltaBytes,
                    lastUpdate = kvp.Value.LastUpdate
                })
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }
}
