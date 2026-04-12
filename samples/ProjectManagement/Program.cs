using Alba;
using Bobcat.Runtime;
using ProjectManagement;
using Wolverine;

return await BobcatRunner.Run(args, r =>
{
    r.ScanForFeatures(typeof(Program).Assembly);
    r.Suite.AddResource(new AlbaResource(
        async () =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<CreateTaskHandler>();
            });
            return await AlbaHost.For(builder, AppBootstrap.MapRoutes);
        },
        reset: _ => { TaskStore.Reset(); return Task.CompletedTask; }));
});
