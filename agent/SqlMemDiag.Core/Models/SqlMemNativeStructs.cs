using System.Runtime.InteropServices;

namespace SqlMemDiag.Core.Models;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct SqlMemProcessEntryNative
{
    public uint Pid;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string ImageName;

    public ulong WorkingSetBytes;
    public ulong PrivateBytes;
    public ulong LockedBytes;
    public ulong LargePageBytes;

    [MarshalAs(UnmanagedType.I1)]
    public bool HasLockPagesPrivilege;
    [MarshalAs(UnmanagedType.I1)]
    public bool IsSqlServer;
    [MarshalAs(UnmanagedType.I1)]
    public bool IsVmmemOrVm;
    [MarshalAs(UnmanagedType.I1)]
    public bool LockedBytesAreExact;
    [MarshalAs(UnmanagedType.I1)]
    public bool LargePageBytesAreExact;
}

[StructLayout(LayoutKind.Sequential)]
public struct SqlMemSummaryNative
{
    public uint Version;
    public uint ProcessCount;
    public ulong TotalPhysBytes;
    public ulong AvailPhysBytes;
    public ulong KernelNonPagedBytes;
    public ulong KernelPagedBytes;
    public ulong SystemCacheBytes;
    [MarshalAs(UnmanagedType.I1)]
    public bool UsesForensicPfns;
    public uint Reserved;
}
