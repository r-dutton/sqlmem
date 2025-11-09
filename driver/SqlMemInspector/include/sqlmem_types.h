#pragma once

#include <ntddk.h>

#define SQLMEM_DEVICE_NAME      L"\\Device\\SqlMemInspector"
#define SQLMEM_DOS_DEVICE_NAME  L"\\DosDevices\\SqlMemInspector"

typedef struct _SQLMEM_PROCESS_ENTRY {
    ULONG   Pid;
    WCHAR   ImageName[64];
    ULONG64 WorkingSetBytes;
    ULONG64 PrivateBytes;
    ULONG64 LockedBytes;
    ULONG64 LargePageBytes;
    BOOLEAN HasLockPagesPrivilege;
    BOOLEAN IsSqlServer;
    BOOLEAN IsVmmemOrVm;
    BOOLEAN LockedBytesAreExact;
    BOOLEAN LargePageBytesAreExact;
} SQLMEM_PROCESS_ENTRY, *PSQLMEM_PROCESS_ENTRY;

#define SQLMEM_SUMMARY_VERSION 1

typedef struct _SQLMEM_SUMMARY {
    ULONG   Version;
    ULONG   ProcessCount;
    ULONG64 TotalPhysBytes;
    ULONG64 AvailPhysBytes;
    ULONG64 KernelNonPagedBytes;
    ULONG64 KernelPagedBytes;
    ULONG64 SystemCacheBytes;
    BOOLEAN UsesForensicPfns;
    ULONG   Reserved;
    SQLMEM_PROCESS_ENTRY Entries[ANYSIZE_ARRAY];
} SQLMEM_SUMMARY, *PSQLMEM_SUMMARY;

#define IOCTL_SQLMEM_GET_SUMMARY CTL_CODE(FILE_DEVICE_UNKNOWN, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS)

NTSTATUS SqlmemCaptureSummary(_Out_writes_bytes_to_(OutputLength, *BytesWritten) PVOID OutputBuffer,
                              _In_ ULONG OutputLength,
                              _Out_ PULONG BytesWritten);

