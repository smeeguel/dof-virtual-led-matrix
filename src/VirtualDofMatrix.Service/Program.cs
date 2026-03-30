using VirtualDofMatrix.Service;
using VirtualDofMatrix.Service.Driver;
using VirtualDofMatrix.Service.Ipc;
using VirtualDofMatrix.Service.Persistence;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "VirtualDofMatrixProvisioning";
});

builder.Services.AddSingleton<PairMetadataStore>();
builder.Services.AddSingleton<IVirtualComDriverController, VirtualComDriverController>();
builder.Services.AddSingleton<ProvisioningService>();
builder.Services.AddHostedService<NamedPipeHostedService>();

var app = builder.Build();
await app.RunAsync();
