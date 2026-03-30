using VirtualDofMatrix.Service.Api;

namespace VirtualDofMatrix.Service.Driver;

public interface IVirtualComDriverController
{
    Task<ServiceResult<bool>> CreateLinkedPairAsync(string txPort, string rxPort, CancellationToken cancellationToken);

    Task<ServiceResult<bool>> DeleteLinkedPairAsync(string txPort, string rxPort, CancellationToken cancellationToken);

    Task<ServiceResult<bool>> GetDriverHealthAsync(CancellationToken cancellationToken);
}
