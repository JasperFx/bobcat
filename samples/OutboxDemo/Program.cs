using Bobcat.Runtime;
using Microsoft.Extensions.Hosting;
using OutboxDemo;
using Wolverine;

return await BobcatRunner.Run(args, r =>
{
    r.ScanForFeatures(typeof(Program).Assembly);
    r.Suite.AddResource(new HostResource(
        async () =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<OrderHandler>()
                    .IncludeType<InventoryHandler>();
            });
            return builder.Build();
        },
        reset: _ =>
        {
            OrderStore.Reset();
            InventoryStore.Reset();
            return Task.CompletedTask;
        }));
});
