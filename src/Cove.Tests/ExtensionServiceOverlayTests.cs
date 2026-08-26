using Cove.Plugins;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cove.Tests;

public sealed class ExtensionServiceOverlayTests
{
    [Fact]
    public void Removing_provider_does_not_dispose_host_singleton()
    {
        var services = CreateHostServices();
        services.AddSingleton<HostDisposable>();
        using var root = services.BuildServiceProvider();
        var host = root.GetRequiredService<HostDisposable>();
        var overlay = CreateOverlay(root, services);

        overlay.BuildProvider("test", new TestExtension(), CreateContext(), (_, _) => { });

        Assert.Same(host, Resolve<HostDisposable>(overlay));

        overlay.Remove("test");
        overlay.Dispose();

        Assert.False(host.IsDisposed);
    }

    [Fact]
    public void Replacing_provider_keeps_same_live_host_singleton()
    {
        var services = CreateHostServices();
        services.AddSingleton<HostDisposable>();
        using var root = services.BuildServiceProvider();
        var host = root.GetRequiredService<HostDisposable>();
        var overlay = CreateOverlay(root, services);
        var extension = new TestExtension();

        overlay.BuildProvider("test", extension, CreateContext(), (_, _) => { });
        Assert.True(overlay.TryGetGeneration("test", extension, out var first));
        overlay.BuildProvider("test", extension, CreateContext(), (_, _) => { });
        Assert.True(overlay.TryGetGeneration("test", extension, out var replacement));
        var forwarded = Resolve<HostDisposable>(overlay);
        overlay.Dispose();

        Assert.NotSame(first, replacement);
        Assert.Same(host, forwarded);
        Assert.False(host.IsDisposed);
    }

    [Fact]
    public void Disposing_overlay_does_not_dispose_host_singleton()
    {
        var services = CreateHostServices();
        services.AddSingleton<HostDisposable>();
        using var root = services.BuildServiceProvider();
        var host = root.GetRequiredService<HostDisposable>();
        var overlay = CreateOverlay(root, services);
        overlay.BuildProvider("test", new TestExtension(), CreateContext(), (_, _) => { });
        _ = Resolve<HostDisposable>(overlay);

        overlay.Dispose();

        Assert.False(host.IsDisposed);
    }

    [Fact]
    public void Disposing_overlay_disposes_extension_created_singleton()
    {
        var services = CreateHostServices();
        using var root = services.BuildServiceProvider();
        var overlay = CreateOverlay(root, services);
        overlay.BuildProvider(
            "test",
            new TestExtension(extensionServices => extensionServices.AddSingleton<ExtensionDisposable>()),
            CreateContext(),
            (_, _) => { });
        var owned = Resolve<ExtensionDisposable>(overlay);

        overlay.Dispose();

        Assert.True(owned.IsDisposed);
    }

    [Fact]
    public void Disposing_overlay_disposes_async_only_extension_singleton()
    {
        var services = CreateHostServices();
        using var root = services.BuildServiceProvider();
        var overlay = CreateOverlay(root, services);
        overlay.BuildProvider(
            "test",
            new TestExtension(extensionServices => extensionServices.AddSingleton<AsyncExtensionDisposable>()),
            CreateContext(),
            (_, _) => { });
        var owned = Resolve<AsyncExtensionDisposable>(overlay);

        overlay.Dispose();

        Assert.True(owned.IsDisposed);
    }

    [Fact]
    public void Keyed_host_singleton_is_shared_and_not_owned_by_overlay()
    {
        var services = CreateHostServices();
        services.AddKeyedSingleton<HostDisposable>("shared", (_, _) => new HostDisposable());
        using var root = services.BuildServiceProvider();
        var host = root.GetRequiredKeyedService<HostDisposable>("shared");
        var overlay = CreateOverlay(root, services);
        overlay.BuildProvider("test", new TestExtension(), CreateContext(), (_, _) => { });

        HostDisposable forwarded;
        using (var scope = overlay.CreateScope("test"))
            forwarded = scope.ServiceProvider.GetRequiredKeyedService<HostDisposable>("shared");
        overlay.Dispose();

        Assert.Same(host, forwarded);
        Assert.False(host.IsDisposed);
    }

    [Fact]
    public void Retired_provider_remains_alive_until_active_scope_drains()
    {
        var services = CreateHostServices();
        using var root = services.BuildServiceProvider();
        var overlay = CreateOverlay(root, services);
        overlay.BuildProvider(
            "test",
            new TestExtension(extensionServices =>
            {
                extensionServices.AddSingleton<ExtensionDisposable>();
                extensionServices.AddScoped<ExtensionScopedDisposable>();
            }),
            CreateContext(),
            (_, _) => { });
        var owned = Resolve<ExtensionDisposable>(overlay);
        var scope = overlay.CreateScope("test");
        var scoped = scope.ServiceProvider.GetRequiredService<ExtensionScopedDisposable>();

        overlay.Remove("test");

        Assert.False(owned.IsDisposed);
        Assert.False(scoped.IsDisposed);
        scope.Dispose();
        Assert.True(scoped.IsDisposed);

        overlay.Dispose();
        Assert.True(owned.IsDisposed);
    }

    [Fact]
    public async Task Replacing_provider_leaves_real_cove_data_source_usable()
    {
        var connectionString = DevboxPostgresConnectionString();
        if (connectionString == null)
            return;

        var services = CreateHostServices();
        services.AddCoveData(connectionString);
        await using var root = services.BuildServiceProvider();
        var dataSource = root.GetRequiredService<NpgsqlDataSource>();
        var overlay = CreateOverlay(root, services);
        var extension = new TestExtension();

        overlay.BuildProvider("test", extension, CreateContext(), (_, _) => { });
        Assert.Same(dataSource, Resolve<NpgsqlDataSource>(overlay));

        overlay.BuildProvider("test", extension, CreateContext(), (_, _) => { });

        Assert.Same(dataSource, Resolve<NpgsqlDataSource>(overlay));
        overlay.Dispose();

        using var scope = root.CreateScope();
        await using var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        await db.Database.OpenConnectionAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(System.Data.ConnectionState.Open, db.Database.GetDbConnection().State);
    }

    [Fact]
    public void Open_generic_host_singleton_is_not_copied_into_overlay()
    {
        var services = CreateHostServices();
        services.AddSingleton(typeof(IOpenGeneric<>), typeof(OpenGeneric<>));
        using var root = services.BuildServiceProvider();
        using var overlay = CreateOverlay(root, services);

        overlay.BuildProvider("test", new TestExtension(), CreateContext(), (_, _) => { });

        using (var scope = overlay.CreateScope("test"))
            Assert.Null(scope.ServiceProvider.GetService<IOpenGeneric<string>>());
        Assert.NotNull(root.GetService<IOpenGeneric<string>>());
    }

    [Fact]
    public void Extension_can_register_and_own_an_open_generic_skipped_from_host()
    {
        var services = CreateHostServices();
        services.AddSingleton(typeof(IOpenGeneric<>), typeof(OpenGeneric<>));
        using var root = services.BuildServiceProvider();
        using var overlay = CreateOverlay(root, services);
        var extension = new TestExtension(extensionServices =>
            extensionServices.AddSingleton(typeof(IOpenGeneric<>), typeof(ExtensionOpenGeneric<>)));

        overlay.BuildProvider("test", extension, CreateContext(), (_, error) => throw error);

        using var scope = overlay.CreateScope("test");
        Assert.IsType<ExtensionOpenGeneric<string>>(
            scope.ServiceProvider.GetRequiredService<IOpenGeneric<string>>());
        Assert.IsType<OpenGeneric<string>>(root.GetRequiredService<IOpenGeneric<string>>());
    }

    private static T Resolve<T>(ExtensionServiceOverlay overlay)
        where T : notnull
    {
        using var scope = overlay.CreateScope("test");
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    private static ServiceCollection CreateHostServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return services;
    }

    private static ExtensionServiceOverlay CreateOverlay(
        IServiceProvider root,
        IServiceCollection services)
        => new(root, services.ToList(), logger: null);

    private static ExtensionContext CreateContext()
        => new()
        {
            Configuration = new ConfigurationBuilder().Build(),
            CoveVersion = "test",
            DataDirectory = Path.GetTempPath(),
        };

    private static string? DevboxPostgresConnectionString()
    {
        var host = Environment.GetEnvironmentVariable("PGHOST");
        var port = Environment.GetEnvironmentVariable("PGPORT");
        var database = Environment.GetEnvironmentVariable("PGDATABASE");
        var username = Environment.GetEnvironmentVariable("PGUSER");
        var password = Environment.GetEnvironmentVariable("PGPASSWORD");
        if (string.IsNullOrWhiteSpace(host)
            || string.IsNullOrWhiteSpace(port)
            || string.IsNullOrWhiteSpace(database)
            || string.IsNullOrWhiteSpace(username))
            return null;

        return new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = int.Parse(port),
            Database = database,
            Username = username,
            Password = password,
            Timeout = 5,
            CommandTimeout = 5,
        }.ConnectionString;
    }

    private sealed class TestExtension(Action<IServiceCollection>? configure = null) : IExtension
    {
        public string Id => "test";
        public string Name => "Test";
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
            => configure?.Invoke(services);
    }

    private sealed class HostDisposable : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    private sealed class ExtensionDisposable : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    private sealed class ExtensionScopedDisposable : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    private sealed class AsyncExtensionDisposable : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private interface IOpenGeneric<T>;

    private sealed class OpenGeneric<T> : IOpenGeneric<T>;
    private sealed class ExtensionOpenGeneric<T> : IOpenGeneric<T>;
}
