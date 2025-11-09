namespace SqlMemDiag.Core.Models;

public sealed record SqlMemProcessEntry
{
    public SqlMemProcessEntry(SqlMemProcessEntryNative native)
    {
        Pid = native.Pid;
        ImageName = string.IsNullOrWhiteSpace(native.ImageName) ? "<unnamed>" : native.ImageName.TrimEnd('\0');
        WorkingSetBytes = native.WorkingSetBytes;
        PrivateBytes = native.PrivateBytes;
        LockedBytes = native.LockedBytes;
        LargePageBytes = native.LargePageBytes;
        HasLockPagesPrivilege = native.HasLockPagesPrivilege;
        IsSqlServer = native.IsSqlServer;
        IsVmmemOrVm = native.IsVmmemOrVm;
        LockedBytesAreExact = native.LockedBytesAreExact;
        LargePageBytesAreExact = native.LargePageBytesAreExact;
    }

    public uint Pid { get; }
    public string ImageName { get; }
    public ulong WorkingSetBytes { get; }
    public ulong PrivateBytes { get; }
    public ulong LockedBytes { get; }
    public ulong LargePageBytes { get; }
    public bool HasLockPagesPrivilege { get; }
    public bool IsSqlServer { get; }
    public bool IsVmmemOrVm { get; }
    public bool LockedBytesAreExact { get; }
    public bool LargePageBytesAreExact { get; }

    public double PrivateMinusWorkingSetGiB => BytesToGiB(PrivateBytes > WorkingSetBytes ? PrivateBytes - WorkingSetBytes : 0);
    public double WorkingSetGiB => BytesToGiB(WorkingSetBytes);
    public double PrivateGiB => BytesToGiB(PrivateBytes);
    public double LockedGiB => BytesToGiB(LockedBytes);
    public double LargePageGiB => BytesToGiB(LargePageBytes);

    private static double BytesToGiB(ulong value) => value / 1024d / 1024d / 1024d;
}
