#include <ntddk.h>
#include <ntstrsafe.h>
#include "include/sqlmem_types.h"

DRIVER_UNLOAD SqlmemUnload;
_Dispatch_type_(IRP_MJ_CREATE) DRIVER_DISPATCH SqlmemCreate;
_Dispatch_type_(IRP_MJ_CLOSE) DRIVER_DISPATCH SqlmemClose;
_Dispatch_type_(IRP_MJ_DEVICE_CONTROL) DRIVER_DISPATCH SqlmemDeviceControl;

static PDEVICE_OBJECT g_SqlmemDeviceObject = NULL;

static VOID
SqlmemInitProcessEntry(
    _Out_ PSQLMEM_PROCESS_ENTRY Entry
    )
{
    RtlZeroMemory(Entry, sizeof(*Entry));
}

static BOOLEAN
SqlmemImageNameEquals(
    _In_opt_ PCUNICODE_STRING ImageName,
    _In_ PCWSTR Target
    )
{
    UNICODE_STRING target;
    UNICODE_STRING baseName;

    if (ImageName == NULL || ImageName->Buffer == NULL || ImageName->Length == 0) {
        return FALSE;
    }

    USHORT lengthInChars = ImageName->Length / sizeof(WCHAR);
    USHORT startIndex = 0;
    for (USHORT i = lengthInChars; i > 0; i--) {
        WCHAR ch = ImageName->Buffer[i - 1];
        if (ch == L'\\' || ch == L'/' || ch == L':') {
            startIndex = i;
            break;
        }
    }

    baseName.Buffer = ImageName->Buffer + startIndex;
    baseName.Length = (lengthInChars - startIndex) * sizeof(WCHAR);
    baseName.MaximumLength = baseName.Length;

    if (baseName.Length == 0) {
        return FALSE;
    }

    RtlInitUnicodeString(&target, Target);
    return RtlEqualUnicodeString(&baseName, &target, TRUE);
}

static BOOLEAN
SqlmemProcessHasLockPagesPrivilege(
    _In_ HANDLE ProcessId
    )
{
    PEPROCESS process = NULL;
    NTSTATUS status = PsLookupProcessByProcessId(ProcessId, &process);
    if (!NT_SUCCESS(status)) {
        return FALSE;
    }

    BOOLEAN hasPrivilege = FALSE;
    LUID lockMemoryLuid = RtlConvertUlongToLuid(SE_LOCK_MEMORY_PRIVILEGE);
    PACCESS_TOKEN token = PsReferencePrimaryToken(process);
    if (token != NULL) {
        PTOKEN_PRIVILEGES privileges = NULL;
        status = SeQueryInformationToken(token, TokenPrivileges, (PVOID*)&privileges);
        if (privileges != NULL) {
            if (NT_SUCCESS(status)) {
                for (ULONG i = 0; i < privileges->PrivilegeCount; i++) {
                    LUID_AND_ATTRIBUTES privilege = privileges->Privileges[i];
                    if (RtlEqualLuid(&privilege.Luid, &lockMemoryLuid)) {
                        if ((privilege.Attributes & (SE_PRIVILEGE_ENABLED | SE_PRIVILEGE_ENABLED_BY_DEFAULT)) != 0) {
                            hasPrivilege = TRUE;
                            break;
                        }
                    }
                }
            }

            ExFreePool(privileges);
        }

        PsDereferencePrimaryToken(token);
    }

    ObDereferenceObject(process);
    return hasPrivilege;
}

static NTSTATUS
SqlmemEnumerateProcesses(
    _Out_writes_bytes_to_(OutputLength, *BytesWritten) PVOID OutputBuffer,
    _In_ ULONG OutputLength,
    _Out_ PULONG BytesWritten
    )
{
    NTSTATUS status;
    ULONG bufferLength = 0;
    PVOID processInfo = NULL;
    ULONG bytesNeeded = 0;
    ULONG processCount = 0;
    PBYTE cursor;
    PSYSTEM_PROCESS_INFORMATION spi;
    PSQLMEM_SUMMARY summary;

    if (OutputBuffer == NULL || BytesWritten == NULL) {
        return STATUS_INVALID_PARAMETER;
    }

    summary = (PSQLMEM_SUMMARY)OutputBuffer;
    if (OutputLength < sizeof(*summary)) {
        return STATUS_BUFFER_TOO_SMALL;
    }

    summary->Version = SQLMEM_SUMMARY_VERSION;
    summary->ProcessCount = 0;
    PPHYSICAL_MEMORY_RANGE ranges = MmGetPhysicalMemoryRanges();
    ULONGLONG totalPhysBytes = 0;
    if (ranges != NULL) {
        for (PPHYSICAL_MEMORY_RANGE current = ranges;
             current->BaseAddress.QuadPart != 0 || current->NumberOfBytes.QuadPart != 0;
             current++) {
            totalPhysBytes += current->NumberOfBytes.QuadPart;
        }
        ExFreePool(ranges);
    }

    SYSTEM_PERFORMANCE_INFORMATION perfInfo = { 0 };
    status = ZwQuerySystemInformation(SystemPerformanceInformation,
                                      &perfInfo,
                                      sizeof(perfInfo),
                                      NULL);
    if (NT_SUCCESS(status)) {
        // SYSTEM_PERFORMANCE_INFORMATION reports these counters in pages.
        summary->AvailPhysBytes = (ULONGLONG)perfInfo.AvailablePages * PAGE_SIZE;
        summary->KernelNonPagedBytes = (ULONGLONG)perfInfo.NonPagedPoolPages * PAGE_SIZE;
        summary->KernelPagedBytes = (ULONGLONG)perfInfo.PagedPoolPages * PAGE_SIZE;
        summary->SystemCacheBytes = (ULONGLONG)perfInfo.ResidentSystemCachePage * PAGE_SIZE;
    } else {
        summary->AvailPhysBytes = 0;
        summary->KernelNonPagedBytes = 0;
        summary->KernelPagedBytes = 0;
        summary->SystemCacheBytes = 0;
    }

    if (totalPhysBytes == 0) {
        SYSTEM_BASIC_INFORMATION basicInfo = { 0 };
        status = ZwQuerySystemInformation(SystemBasicInformation,
                                          &basicInfo,
                                          sizeof(basicInfo),
                                          NULL);
        if (NT_SUCCESS(status)) {
            totalPhysBytes = (ULONGLONG)basicInfo.NumberOfPhysicalPages * PAGE_SIZE;
        }
    }

    summary->TotalPhysBytes = totalPhysBytes;
    summary->UsesForensicPfns = FALSE;
    summary->Reserved = 0;

    bufferLength = 1 << 18; // 256 KB initial buffer

    do {
        processInfo = ExAllocatePoolWithTag(NonPagedPoolNx, bufferLength, 'mIqS');
        if (processInfo == NULL) {
            status = STATUS_INSUFFICIENT_RESOURCES;
            break;
        }

        status = ZwQuerySystemInformation(SystemProcessInformation,
                                          processInfo,
                                          bufferLength,
                                          &bytesNeeded);

        if (status == STATUS_INFO_LENGTH_MISMATCH) {
            ExFreePoolWithTag(processInfo, 'mIqS');
            processInfo = NULL;
            bufferLength = bytesNeeded + (1 << 12);
        }
    } while (status == STATUS_INFO_LENGTH_MISMATCH);

    if (!NT_SUCCESS(status)) {
        if (processInfo != NULL) {
            ExFreePoolWithTag(processInfo, 'mIqS');
        }
        return status;
    }

    cursor = (PBYTE)processInfo;
    while (TRUE) {
        spi = (PSYSTEM_PROCESS_INFORMATION)cursor;
        processCount++;

        if (FIELD_OFFSET(SQLMEM_SUMMARY, Entries) + (processCount * sizeof(SQLMEM_PROCESS_ENTRY)) > OutputLength) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        PSQLMEM_PROCESS_ENTRY entry = &summary->Entries[processCount - 1];
        SqlmemInitProcessEntry(entry);

        entry->Pid = HandleToULong(spi->UniqueProcessId);
        UNICODE_STRING imageName = spi->ImageName;

        if (imageName.Buffer != NULL && imageName.Length > 0) {
            UNICODE_STRING truncated = imageName;
            if (truncated.Length >= sizeof(entry->ImageName)) {
                truncated.Length = sizeof(entry->ImageName) - sizeof(WCHAR);
            }
            RtlStringCchCopyNW(entry->ImageName,
                               RTL_NUMBER_OF(entry->ImageName),
                               truncated.Buffer,
                               truncated.Length / sizeof(WCHAR));
        } else {
            RtlStringCchCopyW(entry->ImageName, RTL_NUMBER_OF(entry->ImageName), L"<System>");
        }

        entry->WorkingSetBytes = spi->WorkingSetSize;
        entry->PrivateBytes = spi->PrivatePageCount;
        entry->HasLockPagesPrivilege = SqlmemProcessHasLockPagesPrivilege(spi->UniqueProcessId);

        if (SqlmemImageNameEquals(&imageName, L"sqlservr.exe")) {
            entry->IsSqlServer = TRUE;
        }

        if (SqlmemImageNameEquals(&imageName, L"vmmem") ||
            SqlmemImageNameEquals(&imageName, L"vmwp.exe")) {
            entry->IsVmmemOrVm = TRUE;
        }

        cursor += spi->NextEntryOffset;
        if (spi->NextEntryOffset == 0) {
            break;
        }
    }

    if (NT_SUCCESS(status)) {
        summary->ProcessCount = processCount;
        *BytesWritten = FIELD_OFFSET(SQLMEM_SUMMARY, Entries) + (processCount * sizeof(SQLMEM_PROCESS_ENTRY));
    } else {
        *BytesWritten = 0;
    }

    if (processInfo != NULL) {
        ExFreePoolWithTag(processInfo, 'mIqS');
    }

    return status;
}

NTSTATUS
SqlmemCaptureSummary(
    _Out_writes_bytes_to_(OutputLength, *BytesWritten) PVOID OutputBuffer,
    _In_ ULONG OutputLength,
    _Out_ PULONG BytesWritten
    )
{
    return SqlmemEnumerateProcesses(OutputBuffer, OutputLength, BytesWritten);
}

_Dispatch_type_(IRP_MJ_CREATE)
NTSTATUS
SqlmemCreate(
    _In_ PDEVICE_OBJECT DeviceObject,
    _Inout_ PIRP Irp
    )
{
    UNREFERENCED_PARAMETER(DeviceObject);

    Irp->IoStatus.Status = STATUS_SUCCESS;
    Irp->IoStatus.Information = 0;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return STATUS_SUCCESS;
}

_Dispatch_type_(IRP_MJ_CLOSE)
NTSTATUS
SqlmemClose(
    _In_ PDEVICE_OBJECT DeviceObject,
    _Inout_ PIRP Irp
    )
{
    UNREFERENCED_PARAMETER(DeviceObject);

    Irp->IoStatus.Status = STATUS_SUCCESS;
    Irp->IoStatus.Information = 0;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return STATUS_SUCCESS;
}

_Dispatch_type_(IRP_MJ_DEVICE_CONTROL)
NTSTATUS
SqlmemDeviceControl(
    _In_ PDEVICE_OBJECT DeviceObject,
    _Inout_ PIRP Irp
    )
{
    UNREFERENCED_PARAMETER(DeviceObject);

    PIO_STACK_LOCATION irpSp = IoGetCurrentIrpStackLocation(Irp);
    ULONG ioControlCode = irpSp->Parameters.DeviceIoControl.IoControlCode;
    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    ULONG bytesWritten = 0;

    switch (ioControlCode) {
    case IOCTL_SQLMEM_GET_SUMMARY:
        status = SqlmemCaptureSummary(Irp->AssociatedIrp.SystemBuffer,
                                      irpSp->Parameters.DeviceIoControl.OutputBufferLength,
                                      &bytesWritten);
        break;

    default:
        status = STATUS_INVALID_DEVICE_REQUEST;
        break;
    }

    Irp->IoStatus.Status = status;
    Irp->IoStatus.Information = bytesWritten;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return status;
}

VOID
SqlmemUnload(
    _In_ PDRIVER_OBJECT DriverObject
    )
{
    UNICODE_STRING symLink;
    RtlInitUnicodeString(&symLink, SQLMEM_DOS_DEVICE_NAME);
    IoDeleteSymbolicLink(&symLink);

    if (g_SqlmemDeviceObject != NULL) {
        IoDeleteDevice(g_SqlmemDeviceObject);
        g_SqlmemDeviceObject = NULL;
    }

    UNREFERENCED_PARAMETER(DriverObject);
}

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPath
    )
{
    UNREFERENCED_PARAMETER(RegistryPath);

    NTSTATUS status;
    UNICODE_STRING deviceName;
    UNICODE_STRING symLink;

    RtlInitUnicodeString(&deviceName, SQLMEM_DEVICE_NAME);
    RtlInitUnicodeString(&symLink, SQLMEM_DOS_DEVICE_NAME);

    status = IoCreateDevice(DriverObject,
                            0,
                            &deviceName,
                            FILE_DEVICE_UNKNOWN,
                            0,
                            FALSE,
                            &g_SqlmemDeviceObject);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    g_SqlmemDeviceObject->Flags |= DO_BUFFERED_IO;

    status = IoCreateSymbolicLink(&symLink, &deviceName);
    if (!NT_SUCCESS(status)) {
        IoDeleteDevice(g_SqlmemDeviceObject);
        g_SqlmemDeviceObject = NULL;
        return status;
    }

    DriverObject->MajorFunction[IRP_MJ_CREATE] = SqlmemCreate;
    DriverObject->MajorFunction[IRP_MJ_CLOSE] = SqlmemClose;
    DriverObject->MajorFunction[IRP_MJ_DEVICE_CONTROL] = SqlmemDeviceControl;
    DriverObject->DriverUnload = SqlmemUnload;

    g_SqlmemDeviceObject->Flags &= ~DO_DEVICE_INITIALIZING;

    return STATUS_SUCCESS;
}

