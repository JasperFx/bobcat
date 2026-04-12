using Alba;
using Bobcat.Runtime;
using CleanArchitectureTodos;

return await BobcatRunner.Run(args, r =>
{
    r.ScanForFeatures(typeof(Program).Assembly);
    r.Suite.AddResource(new AlbaResource(
        async () => await AlbaHost.For(WebApplication.CreateBuilder(), AppBootstrap.MapRoutes),
        reset: _ => { TodoStore.Reset(); return Task.CompletedTask; }));
});
