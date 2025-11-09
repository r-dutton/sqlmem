namespace SqlMemDiag.Core.Models;

public sealed record EtwProcessMemoryStats(long LockedBytesEstimate, long LargePageBytesEstimate, long CommitDeltaBytes, DateTimeOffset LastUpdate);
