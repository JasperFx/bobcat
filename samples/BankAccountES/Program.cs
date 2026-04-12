using Alba;
using Bobcat.Runtime;
using BankAccountES;

return await BobcatRunner.Run(args, r =>
{
    r.ScanForFeatures(typeof(Program).Assembly);
    r.Suite.AddResource(new AlbaResource(
        async () => await AlbaHost.For(WebApplication.CreateBuilder(), AppBootstrap.MapRoutes),
        reset: _ => { AccountStore.Reset(); return Task.CompletedTask; }));
});
