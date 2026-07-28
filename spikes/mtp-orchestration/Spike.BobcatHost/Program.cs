using Microsoft.Testing.Platform.Builder;
using Microsoft.Testing.Platform.Capabilities.TestFramework;
using Spike.BobcatHost;

// This is the whole of "Bobcat exposes itself as an MTP test host". Everything else is the
// framework implementation in BobcatSpecFramework.
var builder = await TestApplication.CreateBuilderAsync(args);

builder.RegisterTestFramework(
    _ => new TestFrameworkCapabilities(),
    (capabilities, serviceProvider) => new BobcatSpecFramework(capabilities, serviceProvider));

using var app = await builder.BuildAsync();
return await app.RunAsync();
