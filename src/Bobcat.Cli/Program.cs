using JasperFx;
using Microsoft.Extensions.Hosting;

// The `bobcat` tool is a plain command host — no web server, no store, nothing that outlives the
// process. JasperFx discovers the [Description]-attributed commands in this assembly.
var builder = Host.CreateApplicationBuilder(args);
return await builder.Build().RunJasperFxCommands(args);
