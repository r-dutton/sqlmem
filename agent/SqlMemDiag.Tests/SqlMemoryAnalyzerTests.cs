using Xunit;
using SqlMemDiag.Core.Analysis;
using SqlMemDiag.Core.Models;

namespace SqlMemDiag.Tests;

public class SqlMemoryAnalyzerTests
{
    [Fact]
    public void DetectsSqlLockedMemory()
    {
        var summary = BuildSummary(128 * GiB, 10 * GiB,
            BuildProcess(pid: 100, name: "sqlservr.exe", workingSet: 20 * GiB, privateBytes: 80 * GiB, locked: 50 * GiB, largePage: 10 * GiB, isSql: true));

        var analyzer = new SqlMemoryAnalyzer();
        var report = analyzer.Analyze(summary, new Dictionary<uint, EtwProcessMemoryStats>());

        Assert.Contains(report.Findings, f => f.Id == "SQL-LPIM");
    }

    [Fact]
    public void DetectsHiddenGapWhenNoCulprit()
    {
        var summary = BuildSummary(64 * GiB, 8 * GiB,
            BuildProcess(pid: 200, name: "other.exe", workingSet: 10 * GiB, privateBytes: 12 * GiB, locked: 0, largePage: 0, isSql: false));

        var analyzer = new SqlMemoryAnalyzer();
        var report = analyzer.Analyze(summary, new Dictionary<uint, EtwProcessMemoryStats>());

        Assert.Contains(report.Findings, f => f.Id == "GAP");
    }

    [Fact]
    public void DetectsVmmemDominance()
    {
        var summary = BuildSummary(64 * GiB, 12 * GiB,
            BuildProcess(pid: 300, name: "vmmem", workingSet: 18 * GiB, privateBytes: 30 * GiB, locked: 0, largePage: 0, isSql: false, isVmmem: true));

        var analyzer = new SqlMemoryAnalyzer();
        var report = analyzer.Analyze(summary, new Dictionary<uint, EtwProcessMemoryStats>());

        Assert.Contains(report.Findings, f => f.Id == "WSL2");
    }

    private static SqlMemSummary BuildSummary(ulong totalPhysical, ulong available, params SqlMemProcessEntry[] processes)
    {
        var native = new SqlMemSummaryNative
        {
            Version = 1,
            ProcessCount = (uint)processes.Length,
            TotalPhysBytes = totalPhysical,
            AvailPhysBytes = available,
            KernelNonPagedBytes = 2 * GiB,
            KernelPagedBytes = 1 * GiB,
            SystemCacheBytes = 4 * GiB,
            UsesForensicPfns = false
        };

        return new SqlMemSummary(native, processes);
    }

    private static SqlMemProcessEntry BuildProcess(uint pid, string name, ulong workingSet, ulong privateBytes, ulong locked, ulong largePage, bool isSql, bool isVmmem = false)
    {
        var native = new SqlMemProcessEntryNative
        {
            Pid = pid,
            ImageName = name,
            WorkingSetBytes = workingSet,
            PrivateBytes = privateBytes,
            LockedBytes = locked,
            LargePageBytes = largePage,
            HasLockPagesPrivilege = isSql,
            IsSqlServer = isSql,
            IsVmmemOrVm = isVmmem,
            LockedBytesAreExact = true,
            LargePageBytesAreExact = true
        };

        return new SqlMemProcessEntry(native);
    }

    private const ulong GiB = 1024UL * 1024UL * 1024UL;
}
