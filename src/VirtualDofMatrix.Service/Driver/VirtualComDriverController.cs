using System.ServiceProcess;
using VirtualDofMatrix.Core.Diagnostics;
using VirtualDofMatrix.Service.Api;

namespace VirtualDofMatrix.Service.Driver;

public sealed class VirtualComDriverController : IVirtualComDriverController
{
    public Task<ServiceResult<bool>> CreateLinkedPairAsync(string txPort, string rxPort, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var health = GetDriverHealth();
        _ = StructuredLogWriter.WriteAsync("service", "driver-create-request", new { txPort, rxPort, health.Success, health.ErrorCode, health.Message });
        if (!health.Success)
        {
            return Task.FromResult(health);
        }

        // TODO: Replace with actual IOCTL exchange against the installed KMDF driver.
        return Task.FromResult(ServiceResult<bool>.Ok(true, "driver pair create accepted"));
    }

    public Task<ServiceResult<bool>> DeleteLinkedPairAsync(string txPort, string rxPort, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var health = GetDriverHealth();
        _ = StructuredLogWriter.WriteAsync("service", "driver-delete-request", new { txPort, rxPort, health.Success, health.ErrorCode, health.Message });
        if (!health.Success)
        {
            return Task.FromResult(health);
        }

        // TODO: Replace with actual IOCTL exchange against the installed KMDF driver.
        return Task.FromResult(ServiceResult<bool>.Ok(true, "driver pair delete accepted"));
    }

    public Task<ServiceResult<bool>> GetDriverHealthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetDriverHealth());
    }

    private static ServiceResult<bool> GetDriverHealth()
    {
        try
        {
            using var service = new ServiceController(ServiceInfo.DriverServiceName);
            var _ = service.Status;
            _ = StructuredLogWriter.WriteAsync("service", "driver-health", new { Result = "reachable" });
            return ServiceResult<bool>.Ok(true, "driver service reachable");
        }
        catch (InvalidOperationException)
        {
            _ = StructuredLogWriter.WriteAsync("service", "driver-health", new { Result = "missing" });
            return ServiceResult<bool>.Fail(ServiceErrorCode.DriverMissing, "Driver service is not installed or unreachable.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 577)
        {
            _ = StructuredLogWriter.WriteAsync("service", "driver-health", new { Result = "signing-blocked", ex.NativeErrorCode });
            return ServiceResult<bool>.Fail(ServiceErrorCode.SigningBlocked, "Windows blocked driver load due to signing policy.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 1275)
        {
            _ = StructuredLogWriter.WriteAsync("service", "driver-health", new { Result = "secure-boot-conflict", ex.NativeErrorCode });
            return ServiceResult<bool>.Fail(ServiceErrorCode.SecureBootConflict, "Driver blocked by Secure Boot/policy configuration.");
        }
        catch (Exception ex)
        {
            _ = StructuredLogWriter.WriteAsync("service", "driver-health", new { Result = "internal-error", ex.Message });
            return ServiceResult<bool>.Fail(ServiceErrorCode.InternalError, $"Unexpected driver check failure: {ex.Message}");
        }
    }
}
