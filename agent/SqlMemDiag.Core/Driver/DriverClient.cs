using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SqlMemDiag.Core.Models;

namespace SqlMemDiag.Core.Driver;

public sealed class DriverClient : IDisposable
{
    private readonly SafeFileHandle _handle;

    public DriverClient()
    {
        _handle = NativeMethods.CreateFile(
            "\\\\.\\SqlMemInspector",
            NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Failed to open \\ \\ . \\ SqlMemInspector. Ensure the driver is installed and loaded.");
        }
    }

    public SqlMemSummary GetSummary()
    {
        int headerSize = Marshal.SizeOf<SqlMemSummaryNative>();
        int entrySize = Marshal.SizeOf<SqlMemProcessEntryNative>();
        int processCapacity = 512;

        while (true)
        {
            int bufferSize = headerSize + (entrySize * processCapacity);
            byte[] buffer = new byte[bufferSize];

            bool success = NativeMethods.DeviceIoControl(
                _handle,
                NativeMethods.IOCTL_SQLMEM_GET_SUMMARY,
                IntPtr.Zero,
                0,
                buffer,
                buffer.Length,
                out int bytesReturned,
                IntPtr.Zero);

            if (!success)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == NativeMethods.ERROR_INSUFFICIENT_BUFFER)
                {
                    processCapacity = checked(processCapacity * 2);
                    continue;
                }

                throw new Win32Exception(error,
                    "Failed to query memory summary from SqlMemInspector.");
            }

            if (bytesReturned < headerSize)
            {
                throw new InvalidOperationException("Driver returned an unexpectedly small buffer.");
            }

            SqlMemSummaryNative header = MemoryMarshal.Read<SqlMemSummaryNative>(buffer.AsSpan(0, headerSize));

            if (header.Version != NativeMethods.SQLMEM_SUMMARY_VERSION)
            {
                throw new NotSupportedException($"Incompatible driver summary version {header.Version}.");
            }

            int requiredProcessCount = checked((int)header.ProcessCount);
            if (requiredProcessCount > processCapacity)
            {
                processCapacity = Math.Max(requiredProcessCount, processCapacity * 2);
                continue;
            }

            if (requiredProcessCount == 0)
            {
                return new SqlMemSummary(header, Array.Empty<SqlMemProcessEntry>());
            }

            int expectedBytes = headerSize + (requiredProcessCount * entrySize);
            if (bytesReturned < expectedBytes)
            {
                throw new InvalidOperationException("Driver response was truncated before all process entries could be read.");
            }

            var entries = new List<SqlMemProcessEntry>(requiredProcessCount);
            ReadOnlySpan<byte> span = buffer.AsSpan(headerSize, requiredProcessCount * entrySize);
            for (int i = 0; i < requiredProcessCount; i++)
            {
                SqlMemProcessEntryNative nativeEntry = MemoryMarshal.Read<SqlMemProcessEntryNative>(span.Slice(i * entrySize, entrySize));
                entries.Add(new SqlMemProcessEntry(nativeEntry));
            }

            return new SqlMemSummary(header, entries);
        }
    }

    public void Dispose()
    {
        _handle.Dispose();
    }
}
