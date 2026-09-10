using System.Net;
using Cove.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.FileProviders;

namespace Cove.Tests;

public sealed class FrontendAssetsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServesMatchingManifestAndRevalidatesEntryRoutes(bool embedded)
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "FrontendFixtures"), "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(directory, Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "FrontendFixtures"), file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            using var physical = new PhysicalFileProvider(directory);
            IFileProvider files = embedded
                ? new ManifestEmbeddedFileProvider(typeof(FrontendAssetsTests).Assembly, "FrontendFixtures")
                : physical;
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            await using var app = builder.Build();
            FrontendAssets.UseBuildEndpoint(app, files);
            // Any accidental fall-through would access app services instead.
            app.Use(async (context, next) =>
            {
                Assert.NotEqual("/api/system/frontend-build", context.Request.Path.Value);
                await next(context);
            });
            FrontendAssets.UseSpa(app, files);
            await app.StartAsync(ct);
            using var client = app.GetTestClient();
            var identity = await client.GetAsync("/api/system/frontend-build", ct);
            Assert.Equal(HttpStatusCode.OK, identity.StatusCode);
            Assert.Equal("no-store", identity.Headers.CacheControl!.ToString());
            Assert.Contains("11111111-1111-4111-8111-111111111111", await identity.Content.ReadAsStringAsync(ct));
            foreach (var route in new[] { "/", "/index.html", "/videos", "/settings/library" })
            {
                var page = await client.GetAsync(route, ct);
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                Assert.Equal("no-cache", page.Headers.CacheControl!.ToString());
                Assert.Contains("fixture", await page.Content.ReadAsStringAsync(ct));
            }
            var stable = await client.GetAsync("/assets/extension-runtime/v1/react.js", ct);
            Assert.Equal("no-cache", stable.Headers.CacheControl!.ToString());
            var hashed = await client.GetAsync("/assets/index-12345678.js", ct);
            Assert.Contains("immutable", hashed.Headers.CacheControl!.ToString());

            if (!embedded)
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "frontend-build.json"), "{\"buildId\":\"22222222-2222-4222-8222-222222222222\"}", ct);
                Assert.Contains("22222222", await client.GetStringAsync("/api/system/frontend-build", ct));
                foreach (var invalid in new[] { "{", "null", "{}", "{\"buildId\":12}", "{\"buildId\":\"invalid\"}" })
                {
                    await File.WriteAllTextAsync(Path.Combine(directory, "frontend-build.json"), invalid, ct);
                    var unavailable = await client.GetAsync("/api/system/frontend-build", ct);
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
                    Assert.True(unavailable.Headers.CacheControl!.NoStore);
                }
                File.Delete(Path.Combine(directory, "frontend-build.json"));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/system/frontend-build", ct)).StatusCode);
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
