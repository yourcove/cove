using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;

namespace Cove.Api;

public static partial class FrontendAssets
{
    public static IFileProvider ResolveProvider()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        return Directory.Exists(path)
            ? new PhysicalFileProvider(path)
            : new ManifestEmbeddedFileProvider(typeof(FrontendAssets).Assembly, "wwwroot");
    }

    // Run before database and principal middleware: this public build identity
    // must remain available during maintenance and does not resolve any services.
    public static void UseBuildEndpoint(IApplicationBuilder app, IFileProvider files)
    {
        app.Use(async (context, next) =>
        {
            if (!HttpMethods.IsGet(context.Request.Method)
                || context.Request.Path != "/api/system/frontend-build")
            {
                await next(context);
                return;
            }

            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var file = files.GetFileInfo("frontend-build.json");
                if (file.Exists)
                {
                    await using var stream = file.CreateReadStream();
                    using var manifest = await JsonDocument.ParseAsync(stream, cancellationToken: context.RequestAborted);
                    if (manifest.RootElement.ValueKind == JsonValueKind.Object
                        && manifest.RootElement.TryGetProperty("buildId", out var id)
                        && id.ValueKind == JsonValueKind.String
                        && Guid.TryParseExact(id.GetString(), "D", out _))
                    {
                        await context.Response.WriteAsJsonAsync(new { buildId = id.GetString() }, context.RequestAborted);
                        return;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                // A missing/partially replaced manifest is unavailable, not an update.
            }
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        });
    }

    public static void UseSpa(WebApplication app, IFileProvider files)
    {
        var options = new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = context =>
            {
                var path = context.Context.Request.Path.Value ?? "";
                context.Context.Response.Headers.CacheControl =
                    !path.StartsWith("/assets/extension-runtime/", StringComparison.OrdinalIgnoreCase)
                    && HashedAsset().IsMatch(path)
                        ? "public, max-age=31536000, immutable"
                        : "no-cache";
            },
        };
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(options);
        app.MapFallbackToFile("index.html", options);
    }

    [GeneratedRegex(@"^/assets/[^/]+-[A-Za-z0-9_-]{8,}\.(?:js|css|woff2?|png|jpe?g|svg|webp|avif)$", RegexOptions.CultureInvariant)]
    private static partial Regex HashedAsset();
}
