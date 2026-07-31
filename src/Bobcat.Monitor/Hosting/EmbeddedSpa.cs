using Microsoft.Extensions.FileProviders;

namespace Bobcat.Monitor.Hosting;

/// <summary>
/// Serves the Vue SPA from resources embedded by the csproj's EmbedFrontend path — the
/// CritterWatch EmbeddedSpaMiddleware pattern, minus its sub-path mounting (this host is a
/// standalone tool; the SPA owns the root). When nothing is embedded (a plain dev build,
/// where Vite serves the frontend), the whole thing no-ops.
/// </summary>
public static class EmbeddedSpa
{
    public static IApplicationBuilder UseBobcatMonitorSpa(this IApplicationBuilder app)
    {
        var assembly = typeof(EmbeddedSpa).Assembly;
        var prefix = resolveResourcePrefix(assembly);

        var indexHtml = loadIndexHtml(assembly, prefix);
        if (indexHtml == null) return app;

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new EmbeddedFileProvider(assembly, prefix)
        });

        // SPA fallback: any unmatched GET that isn't an API route gets index.html, so the Vue
        // Router's history-mode routes (/runs/{id}) survive a hard refresh.
        app.Use(async (context, next) =>
        {
            await next();

            if (context.Response.StatusCode == 404
                && !context.Response.HasStarted
                && HttpMethods.IsGet(context.Request.Method)
                && !context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/html";
                await context.Response.Body.WriteAsync(indexHtml);
            }
        });

        return app;
    }

    /// <summary>
    /// Embedded names are "{RootNamespace}.wwwroot.<path>" — derive the prefix from the actual
    /// manifest rather than assuming the assembly name, the lesson CritterWatch learned when a
    /// second package flavor pinned RootNamespace away from its assembly name.
    /// </summary>
    private static string resolveResourcePrefix(System.Reflection.Assembly assembly)
    {
        const string indexSuffix = ".wwwroot.index.html";
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (name.EndsWith(indexSuffix, StringComparison.Ordinal))
            {
                return name[..^".index.html".Length];
            }
        }

        return assembly.GetName().Name + ".wwwroot";
    }

    private static byte[]? loadIndexHtml(System.Reflection.Assembly assembly, string prefix)
    {
        using var stream = assembly.GetManifestResourceStream(prefix + ".index.html");
        if (stream == null) return null;

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
