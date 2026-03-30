using System.ServiceProcess;
using VirtualDofMatrix.Service.Api;

namespace VirtualDofMatrix.Service.Driver;

public sealed class VirtualComDriverController : IVirtualComDriverController
{
    public Task<ServiceResult<bool>> CreateLinkedPairAsync(string txPort, string rxPort, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var health = GetDriverHealth();
        if (!health.Success)
        {
            return Task.FromResult(health);
        }

        // TODO: Replace with actual IOCTL exchange against the installed KMDF driver.
        // Placeholder assumes pair creation succeeds once driver service is reachable.
        _ = txPort;
        _ = rxPort;

        return Task.FromResult(ServiceResult<bool>.Ok(true, "driver pair create accepted"));
    }

    public Task<ServiceResult<bool>> DeleteLinkedPairAsync(string txPort, string rxPort, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var health = GetDriverHealth();
        if (!health.Success)
        {
            return Task.FromResult(health);
        }

        // TODO: Replace with actual IOCTL exchange against the installed KMDF driver.
        _ = txPort;
        _ = rxPort;

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
            return ServiceResult<bool>.Ok(true, "driver service reachable");
        }
        catch (InvalidOperationException)
        {
            return ServiceResult<bool>.Fail(ServiceErrorCode.DriverMissing, "Driver service is not installed or unreachable.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 577)
        {
            return ServiceResult<bool>.Fail(ServiceErrorCode.SigningBlocked, "Windows blocked driver load due to signing policy.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 1275)
        {
            return ServiceResult<bool>.Fail(ServiceErrorCode.SecureBootConflict, "Driver blocked by Secure Boot/policy configuration.");
        }
        catch (Exception ex)
        {
            return ServiceResult<bool>.Fail(ServiceErrorCode.InternalError, $"Unexpected driver check failure: {ex.Message}");
        }
    }
}
