using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using SqlMemDiag.Core.Models;

namespace SqlMemDiag.Core.Etw;

public sealed class EtwMemoryTracker : IDisposable
{
    private const int MemLargePages = 0x20000000;
    private const int MemPhysical = 0x00400000;
    private static readonly TimeSpan StatsStaleThreshold = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<uint, EtwProcessMemoryStats> _stats = new();
    private readonly TraceEventSession _session;
    private readonly Task _processingTask;

    public EtwMemoryTracker(string? sessionName = null)
    {
        string traceName = sessionName ?? $"SqlMemDiag-Session-{Guid.NewGuid():N}";
        _session = new TraceEventSession(traceName)
        {
            StopOnDispose = true
        };

        _session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Memory | KernelTraceEventParser.Keywords.VirtualAlloc);

        _session.Source.Kernel.VirtualMemAlloc += OnVirtualAlloc;
        _session.Source.Kernel.VirtualMemFree += OnVirtualFree;
        _session.Source.Kernel.ProcessStop += OnProcessStop;
        _session.Source.Kernel.ProcessDCStop += OnProcessStop;

        _processingTask = Task.Factory.StartNew(
            ProcessEvents,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void ProcessEvents()
    {
        try
        {
            _session.Source.Process();
        }
        catch (ObjectDisposedException)
        {
            // Session disposed while processing.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[warn] ETW session terminated unexpectedly: {ex.Message}");
        }
    }

    private void OnVirtualAlloc(VirtualAllocTraceData data)
    {
        if (data.ProcessID <= 0)
        {
            return;
        }

        long size = GetLongPayload(data, "Size");
        if (size == 0)
        {
            size = data.Length;
        }

        int allocType = GetIntPayload(data, "AllocType");
        if (allocType == 0)
        {
            allocType = (int)data.Flags;
        }

        bool isLargePage = (allocType & MemLargePages) == MemLargePages;
        bool isPhysical = (allocType & MemPhysical) == MemPhysical;
        bool isLocked = isLargePage || isPhysical;

        _stats.AddOrUpdate(
            (uint)data.ProcessID,
            _ => new EtwProcessMemoryStats(
                isLocked ? size : 0,
                isLargePage ? size : 0,
                size,
                DateTimeOffset.UtcNow),
            (_, current) => new EtwProcessMemoryStats(
                current.LockedBytesEstimate + (isLocked ? size : 0),
                current.LargePageBytesEstimate + (isLargePage ? size : 0),
                current.CommitDeltaBytes + size,
                DateTimeOffset.UtcNow));
    }

    private void OnVirtualFree(VirtualAllocTraceData data)
    {
        if (data.ProcessID <= 0)
        {
            return;
        }

        long size = -GetLongPayload(data, "Size");
        if (size == 0)
        {
            size = -data.Length;
        }

        int freeType = GetIntPayload(data, "FreeType");
        if (freeType == 0)
        {
            freeType = (int)data.Flags;
        }

        bool isLargePage = (freeType & MemLargePages) == MemLargePages;
        bool isPhysical = (freeType & MemPhysical) == MemPhysical;
        bool affectsLocked = isLargePage || isPhysical;

        _stats.AddOrUpdate(
            (uint)data.ProcessID,
            _ => new EtwProcessMemoryStats(0, 0, size, DateTimeOffset.UtcNow),
            (_, current) => new EtwProcessMemoryStats(
                ClampToZero(current.LockedBytesEstimate + (affectsLocked ? size : 0)),
                ClampToZero(current.LargePageBytesEstimate + (isLargePage ? size : 0)),
                current.CommitDeltaBytes + size,
                DateTimeOffset.UtcNow));
    }

    private static long ClampToZero(long value) => value < 0 ? 0 : value;

    private void OnProcessStop(ProcessTraceData data)
    {
        if (data.ProcessID <= 0)
        {
            return;
        }

        _stats.TryRemove((uint)data.ProcessID, out _);
    }

    private static int GetIntPayload(TraceEvent data, string name)
    {
        int index = data.PayloadIndex(name);
        if (index < 0)
        {
            return 0;
        }

        object? value = data.PayloadValue(index);
        if (value is null)
        {
            return 0;
        }

        try
        {
            return Convert.ToInt32(value);
        }
        catch (FormatException)
        {
            return 0;
        }
        catch (InvalidCastException)
        {
            return 0;
        }
    }

    private static long GetLongPayload(TraceEvent data, string name)
    {
        int index = data.PayloadIndex(name);
        if (index < 0)
        {
            return 0;
        }

        object? value = data.PayloadValue(index);
        if (value is null)
        {
            return 0;
        }

        try
        {
            return value switch
            {
                long l => l,
                ulong ul => unchecked((long)ul),
                int i => i,
                uint ui => ui,
                short s => s,
                ushort us => us,
                _ => Convert.ToInt64(value)
            };
        }
        catch (FormatException)
        {
            return 0;
        }
        catch (InvalidCastException)
        {
            return 0;
        }
        catch (OverflowException)
        {
            return 0;
        }
    }

    public IReadOnlyDictionary<uint, EtwProcessMemoryStats> BuildSnapshot()
    {
        RemoveStaleEntries(DateTimeOffset.UtcNow - StatsStaleThreshold);
        return _stats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private void RemoveStaleEntries(DateTimeOffset threshold)
    {
        foreach (var kvp in _stats)
        {
            if (kvp.Value.LastUpdate < threshold)
            {
                _stats.TryRemove(kvp.Key, out _);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _session.Source.StopProcessing();
        }
        catch (ObjectDisposedException)
        {
            // Session already stopped or disposed.
        }

        _session.Dispose();
        try
        {
            _processingTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Swallow exceptions thrown during shutdown.
        }
    }
}
