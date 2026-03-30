using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using VirtualDofMatrix.Service.Api;

namespace VirtualDofMatrix.Service.Ipc;

public sealed class NamedPipeHostedService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ProvisioningService _provisioningService;
    private readonly ILogger<NamedPipeHostedService> _logger;

    public NamedPipeHostedService(ProvisioningService provisioningService, ILogger<NamedPipeHostedService> logger)
    {
        _provisioningService = provisioningService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Named pipe API listening on {PipeName}", ServiceInfo.PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            using var server = CreatePipeServer();
            await server.WaitForConnectionAsync(stoppingToken);
            await ProcessRequestAsync(server, stoppingToken);
        }
    }

    private static NamedPipeServerStream CreatePipeServer()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName: ServiceInfo.PipeName,
            direction: PipeDirection.InOut,
            maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            pipeSecurity: security);
    }

    private async Task ProcessRequestAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        var requestLine = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            await WriteAsync(writer, ServiceResult<object>.Fail(ServiceErrorCode.ValidationFailed, "Empty request."), cancellationToken);
            return;
        }

        PipeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<PipeRequest>(requestLine, JsonOptions);
        }
        catch (JsonException ex)
        {
            await WriteAsync(writer, ServiceResult<object>.Fail(ServiceErrorCode.ValidationFailed, $"Invalid JSON request: {ex.Message}"), cancellationToken);
            return;
        }

        if (request is null)
        {
            await WriteAsync(writer, ServiceResult<object>.Fail(ServiceErrorCode.ValidationFailed, "Missing request payload."), cancellationToken);
            return;
        }

        switch (request.Command.Trim().ToLowerInvariant())
        {
            case "create":
                await WriteAsync(writer,
                    await _provisioningService.CreatePairAsync(request.TxPort ?? string.Empty, request.RxPort ?? string.Empty, cancellationToken),
                    cancellationToken);
                break;

            case "delete":
                await WriteAsync(writer,
                    await _provisioningService.DeletePairAsync(request.TxPort ?? string.Empty, request.RxPort ?? string.Empty, cancellationToken),
                    cancellationToken);
                break;

            case "list":
                await WriteAsync(writer, ServiceResult<IReadOnlyList<PairMetadata>>.Ok(await _provisioningService.ListPairsAsync(cancellationToken)), cancellationToken);
                break;

            case "health":
                await WriteAsync(writer, ServiceResult<HealthInfo>.Ok(await _provisioningService.GetHealthAsync(cancellationToken)), cancellationToken);
                break;

            default:
                await WriteAsync(writer, ServiceResult<object>.Fail(ServiceErrorCode.ValidationFailed, "Unsupported command."), cancellationToken);
                break;
        }
    }

    private static Task WriteAsync<T>(StreamWriter writer, ServiceResult<T> response, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(response, JsonOptions);
        return writer.WriteLineAsync(json.AsMemory(), cancellationToken);
    }
}
