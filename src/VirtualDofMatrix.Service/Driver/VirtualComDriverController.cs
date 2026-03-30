using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VirtualDofMatrix.Core.Diagnostics;
using VirtualDofMatrix.Service.Api;

namespace VirtualDofMatrix.Service.Driver;

public sealed class VirtualComDriverController : IVirtualComDriverController
{
    private const uint IoctlGetHealth = 0x80102000;
    private const uint IoctlCreatePair = 0x8010A004;
    private const uint IoctlDeletePair = 0x8010A008;
    private const uint IoctlVersion = 1;
    private const string DevicePath = "\\\\.\\VirtualDofMatrixSerial";

    public Task<ServiceResult<bool>> CreateLinkedPairAsync(string txPort, string rxPort, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SendPairRequest(IoctlCreatePair, txPort, rxPort, "driver-create-request"));
    }

    public Task<ServiceResult<bool>> DeleteLinkedPairAsync(string txPort, string rxPort, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SendPairRequest(IoctlDeletePair, txPort, rxPort, "driver-delete-request"));
    }

    public Task<ServiceResult<bool>> GetDriverHealthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var handle = OpenDriverHandle();
            var response = SendIoctl(handle, IoctlGetHealth, null);
            var success = response.Status == 0;

            _ = StructuredLogWriter.WriteAsync("service", "driver-health", new { success, response.Status, response.PairCount });

            return Task.FromResult(success
                ? ServiceResult<bool>.Ok(true, "driver reachable")
                : ServiceResult<bool>.Fail(ServiceErrorCode.InternalError, $"Driver returned status 0x{response.Status:X8}"));
        }
        catch (Win32Exception ex)
        {
            var mapped = MapWin32Error(ex.NativeErrorCode);
            _ = StructuredLogWriter.WriteAsync("service", "driver-health", new { success = false, ex.NativeErrorCode, mapped.ErrorCode, mapped.Message });
            return Task.FromResult(mapped);
        }
        catch (Exception ex)
        {
            _ = StructuredLogWriter.WriteAsync("service", "driver-health", new { success = false, ex.Message });
            return Task.FromResult(ServiceResult<bool>.Fail(ServiceErrorCode.InternalError, ex.Message));
        }
    }

    private static ServiceResult<bool> SendPairRequest(uint ioctl, string txPort, string rxPort, string logEvent)
    {
        try
        {
            using var handle = OpenDriverHandle();
            var request = new PairRequest
            {
                Version = IoctlVersion,
                TxPort = txPort,
                RxPort = rxPort,
            };

            var response = SendIoctl(handle, ioctl, request);
            var success = response.Status == 0;
            _ = StructuredLogWriter.WriteAsync("service", logEvent, new { txPort, rxPort, success, response.Status, response.PairCount });

            return success
                ? ServiceResult<bool>.Ok(true, "ok")
                : ServiceResult<bool>.Fail(ServiceErrorCode.InternalError, $"Driver returned status 0x{response.Status:X8}");
        }
        catch (Win32Exception ex)
        {
            var mapped = MapWin32Error(ex.NativeErrorCode);
            _ = StructuredLogWriter.WriteAsync("service", logEvent, new { txPort, rxPort, success = false, ex.NativeErrorCode, mapped.ErrorCode, mapped.Message });
            return mapped;
        }
    }

    private static DriverResponse SendIoctl(SafeFileHandle handle, uint ioctl, PairRequest? request)
    {
        var outSize = Marshal.SizeOf<DriverResponse>();
        var outBuffer = Marshal.AllocHGlobal(outSize);

        var inSize = 0;
        IntPtr inBuffer = IntPtr.Zero;

        try
        {
            if (request is not null)
            {
                inSize = Marshal.SizeOf<PairRequest>();
                inBuffer = Marshal.AllocHGlobal(inSize);
                Marshal.StructureToPtr(request, inBuffer, false);
            }

            if (!DeviceIoControl(handle, ioctl, inBuffer, inSize, outBuffer, outSize, out var bytesReturned, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (bytesReturned < outSize)
            {
                throw new InvalidOperationException($"Driver IOCTL returned insufficient bytes: {bytesReturned}.");
            }

            return Marshal.PtrToStructure<DriverResponse>(outBuffer);
        }
        finally
        {
            if (inBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inBuffer);
            }

            Marshal.FreeHGlobal(outBuffer);
        }
    }

    private static SafeFileHandle OpenDriverHandle()
    {
        var handle = CreateFile(DevicePath, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return handle;
    }

    private static ServiceResult<bool> MapWin32Error(int nativeError)
        => nativeError switch
        {
            2 => ServiceResult<bool>.Fail(ServiceErrorCode.DriverMissing, "Driver device path not found."),
            577 => ServiceResult<bool>.Fail(ServiceErrorCode.SigningBlocked, "Windows blocked driver load due to signing policy."),
            1275 => ServiceResult<bool>.Fail(ServiceErrorCode.SecureBootConflict, "Driver blocked by Secure Boot/policy configuration."),
            _ => ServiceResult<bool>.Fail(ServiceErrorCode.InternalError, $"Driver call failed: Win32={nativeError}"),
        };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PairRequest
    {
        public uint Version;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string TxPort;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string RxPort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DriverResponse
    {
        public uint Version;
        public int Status;
        public uint PairCount;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}
