using Cove.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Tests;

public sealed class ExtensionServiceOverlayLifecycleTests
{
    [Fact]
    public void Replaced_provider_without_active_scopes_is_disposed_immediately()
    {
        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        using var root = hostServices.BuildServiceProvider();
        using var overlay = new ExtensionServiceOverlay(root, hostServices.ToList(), logger: null);
        var extension = new DisposableServiceExtension();

        var first = new DisposalProbe();
        extension.Service = first;
        overlay.BuildProvider(extension.Id, extension, CreateContext(), (_, error) => throw error);
        using (var firstScope = overlay.CreateScope(extension.Id))
            Assert.Same(first, firstScope.ServiceProvider.GetRequiredService<DisposalProbe>());

        extension.Service = new DisposalProbe();
        overlay.BuildProvider(extension.Id, extension, CreateContext(), (_, error) => throw error);

        Assert.Equal(1, first.DisposeCount);
    }

    [Fact]
    public void Configure_failure_keeps_previous_provider_in_place()
    {
        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        using var root = hostServices.BuildServiceProvider();
        using var overlay = new ExtensionServiceOverlay(root, hostServices.ToList(), logger: null);
        var extension = new DisposableServiceExtension();

        var first = new DisposalProbe();
        extension.Service = first;
        overlay.BuildProvider(extension.Id, extension, CreateContext(), (_, error) => throw error);

        Exception? configureFailure = null;
        extension.ThrowOnConfigure = true;
        var built = overlay.TryBuildProvider(extension.Id, extension, CreateContext(), (_, error) => configureFailure = error);

        Assert.False(built);
        Assert.IsType<InvalidOperationException>(configureFailure);
        Assert.Equal(0, first.DisposeCount);
        using var currentScope = overlay.CreateScope(extension.Id);
        Assert.Same(first, currentScope.ServiceProvider.GetRequiredService<DisposalProbe>());
    }

    [Fact]
    public async Task Configure_failure_stops_runtime_initialization_and_retires_provider()
    {
        var manager = new ExtensionManager(CreateContext());
        var extension = new DisposableServiceExtension();
        manager.Register(extension, "local");
        MarkAsRuntimeExtension(manager, extension.Id);

        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        manager.CaptureHostServices(hostServices);
        using var root = hostServices.BuildServiceProvider();
        manager.PrepareRuntimeServices(root);

        var first = extension.Service;
        using (var scope = manager.CreateExtensionScope(extension.Id))
            Assert.Same(first, scope.ServiceProvider.GetRequiredService<DisposalProbe>());
        extension.ThrowOnConfigure = true;

        Assert.False(await manager.InitializeExtensionAsync(extension.Id, root));
        Assert.False(manager.IsEnabled(extension.Id));
        Assert.Equal(1, first.DisposeCount);
        Assert.Throws<InvalidOperationException>(() => manager.CreateExtensionScope(extension.Id));
    }

    [Fact]
    public void Configure_failure_during_repeated_runtime_preparation_retires_provider()
    {
        var manager = new ExtensionManager(CreateContext());
        var extension = new DisposableServiceExtension();
        manager.Register(extension, "local");
        MarkAsRuntimeExtension(manager, extension.Id);

        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        manager.CaptureHostServices(hostServices);
        using var root = hostServices.BuildServiceProvider();
        manager.PrepareRuntimeServices(root);

        var first = extension.Service;
        using (var scope = manager.CreateExtensionScope(extension.Id))
            Assert.Same(first, scope.ServiceProvider.GetRequiredService<DisposalProbe>());
        extension.ThrowOnConfigure = true;

        manager.PrepareRuntimeServices(root);

        Assert.False(manager.IsEnabled(extension.Id));
        Assert.Equal(1, first.DisposeCount);
        Assert.Throws<InvalidOperationException>(() => manager.CreateExtensionScope(extension.Id));
    }

    [Fact]
    public void Replaced_provider_is_disposed_after_its_last_scope_drains()
    {
        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        using var root = hostServices.BuildServiceProvider();
        using var overlay = new ExtensionServiceOverlay(root, hostServices.ToList(), logger: null);
        var extension = new DisposableServiceExtension();

        var first = new DisposalProbe();
        extension.Service = first;
        overlay.BuildProvider(extension.Id, extension, CreateContext(), (_, error) => throw error);
        var activeScope = overlay.CreateScope(extension.Id);
        Assert.Same(first, activeScope.ServiceProvider.GetRequiredService<DisposalProbe>());

        var second = new DisposalProbe();
        extension.Service = second;
        overlay.BuildProvider(extension.Id, extension, CreateContext(), (_, error) => throw error);

        Assert.Equal(0, first.DisposeCount);
        Assert.Equal(0, second.DisposeCount);

        activeScope.Dispose();
        activeScope.Dispose();

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, second.DisposeCount);

        using (var currentScope = overlay.CreateScope(extension.Id))
            Assert.Same(second, currentScope.ServiceProvider.GetRequiredService<DisposalProbe>());

        overlay.Dispose();

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public void Removed_provider_is_disposed_after_its_last_scope_drains()
    {
        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        using var root = hostServices.BuildServiceProvider();
        using var overlay = new ExtensionServiceOverlay(root, hostServices.ToList(), logger: null);
        var extension = new DisposableServiceExtension();

        var service = new DisposalProbe();
        extension.Service = service;
        overlay.BuildProvider(extension.Id, extension, CreateContext(), (_, error) => throw error);
        var activeScope = overlay.CreateScope(extension.Id);
        Assert.Same(service, activeScope.ServiceProvider.GetRequiredService<DisposalProbe>());

        overlay.Remove(extension.Id);

        Assert.Equal(0, service.DisposeCount);
        Assert.Throws<InvalidOperationException>(() => overlay.CreateScope(extension.Id));

        activeScope.Dispose();

        Assert.Equal(1, service.DisposeCount);
    }

    [Fact]
    public void Disposed_overlay_retires_provider_until_its_last_scope_drains()
    {
        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        using var root = hostServices.BuildServiceProvider();
        var overlay = new ExtensionServiceOverlay(root, hostServices.ToList(), logger: null);
        var extension = new DisposableServiceExtension();

        var service = new DisposalProbe();
        extension.Service = service;
        overlay.BuildProvider(extension.Id, extension, CreateContext(), (_, error) => throw error);
        var activeScope = overlay.CreateScope(extension.Id);
        Assert.Same(service, activeScope.ServiceProvider.GetRequiredService<DisposalProbe>());

        overlay.Dispose();

        Assert.Equal(0, service.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => overlay.CreateScope(extension.Id));

        activeScope.Dispose();

        Assert.Equal(1, service.DisposeCount);
    }

    [Fact]
    public void Retired_provider_disposes_async_only_services()
    {
        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        using var root = hostServices.BuildServiceProvider();
        using var overlay = new ExtensionServiceOverlay(root, hostServices.ToList(), logger: null);
        var extension = new AsyncDisposableServiceExtension();

        var first = new AsyncDisposalProbe();
        extension.Service = first;
        overlay.BuildProvider(extension.Id, extension, CreateContext(), (_, error) => throw error);
        using (var scope = overlay.CreateScope(extension.Id))
            Assert.Same(first, scope.ServiceProvider.GetRequiredService<AsyncDisposalProbe>());

        extension.Service = new AsyncDisposalProbe();
        overlay.BuildProvider(extension.Id, extension, CreateContext(), (_, error) => throw error);

        Assert.Equal(1, first.DisposeAsyncCount);
    }

    [Fact]
    public void Nested_extension_scope_keeps_retired_provider_alive_until_it_drains()
    {
        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        using var root = hostServices.BuildServiceProvider();
        using var overlay = new ExtensionServiceOverlay(root, hostServices.ToList(), logger: null);
        var extension = new DisposableServiceExtension();

        var first = new DisposalProbe();
        extension.Service = first;
        overlay.BuildProvider(extension.Id, extension, CreateContext(), (_, error) => throw error);
        var outerScope = overlay.CreateScope(extension.Id);
        Assert.Same(first, outerScope.ServiceProvider.GetRequiredService<DisposalProbe>());
        var nestedFactory = outerScope.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
        var injectedFactory = outerScope.ServiceProvider
            .GetRequiredService<ScopeFactoryConsumer>()
            .ScopeFactory;
        var nestedScope = nestedFactory.CreateScope();
        var injectedScope = injectedFactory.CreateScope();

        extension.Service = new DisposalProbe();
        overlay.BuildProvider(extension.Id, extension, CreateContext(), (_, error) => throw error);
        outerScope.Dispose();

        Assert.Equal(0, first.DisposeCount);
        Assert.Throws<InvalidOperationException>(() => nestedFactory.CreateScope());
        Assert.Throws<InvalidOperationException>(() => injectedFactory.CreateScope());

        nestedScope.Dispose();
        Assert.Equal(0, first.DisposeCount);
        injectedScope.Dispose();

        Assert.Equal(1, first.DisposeCount);
    }

    [Fact]
    public void Execution_lease_exposes_persistent_provider_facade_and_pins_its_generation()
    {
        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        using var root = hostServices.BuildServiceProvider();
        using var overlay = new ExtensionServiceOverlay(root, hostServices.ToList(), logger: null);
        var extension = new DisposableServiceExtension();

        var first = new DisposalProbe();
        extension.Service = first;
        overlay.BuildProvider(extension.Id, extension, CreateContext(), (_, error) => throw error);
        Assert.True(overlay.TryGetGeneration(extension.Id, extension, out var generation));
        Assert.True(overlay.TryCreateLease(extension.Id, extension, generation, out var lease));
        var capturedServices = lease.Services;
        Assert.Same(first, capturedServices.GetRequiredService<DisposalProbe>());

        extension.Service = new DisposalProbe();
        overlay.BuildProvider(extension.Id, extension, CreateContext(), (_, error) => throw error);

        Assert.Equal(0, first.DisposeCount);
        Assert.Same(first, capturedServices.GetRequiredService<DisposalProbe>());
        Assert.Throws<InvalidOperationException>(() =>
            capturedServices.GetRequiredService<IExtensionServiceScopeFactory>().CreateScope());

        lease.Dispose();

        Assert.Equal(1, first.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => capturedServices.GetRequiredService<DisposalProbe>());
    }

    [Fact]
    public void Stale_extension_instance_cannot_lease_replacement_provider_generation()
    {
        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        using var root = hostServices.BuildServiceProvider();
        using var overlay = new ExtensionServiceOverlay(root, hostServices.ToList(), logger: null);
        var oldExtension = new DisposableServiceExtension();
        overlay.BuildProvider(oldExtension.Id, oldExtension, CreateContext(), (_, error) => throw error);
        Assert.True(overlay.TryGetGeneration(oldExtension.Id, oldExtension, out var oldGeneration));

        var replacement = new DisposableServiceExtension();
        overlay.BuildProvider(replacement.Id, replacement, CreateContext(), (_, error) => throw error);
        Assert.True(overlay.TryGetGeneration(replacement.Id, replacement, out var replacementGeneration));

        Assert.False(overlay.TryCreateLease(oldExtension.Id, oldExtension, oldGeneration, out _));
        Assert.False(overlay.TryCreateScope(oldExtension.Id, oldExtension, oldGeneration, out _));
        Assert.Null(overlay.GetProviderForEndpointBuild(oldExtension.Id, oldExtension, oldGeneration));

        Assert.True(overlay.TryCreateLease(replacement.Id, replacement, replacementGeneration, out var lease));
        lease.Dispose();
        using var scope = overlay.CreateScope(replacement.Id);
        Assert.Same(replacement.Service, scope.ServiceProvider.GetRequiredService<DisposalProbe>());
    }

    [Fact]
    public void Stale_generation_cannot_lease_rebuilt_provider_for_same_extension_instance()
    {
        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        using var root = hostServices.BuildServiceProvider();
        using var overlay = new ExtensionServiceOverlay(root, hostServices.ToList(), logger: null);
        var extension = new DisposableServiceExtension();
        overlay.BuildProvider(extension.Id, extension, CreateContext(), (_, error) => throw error);
        Assert.True(overlay.TryGetGeneration(extension.Id, extension, out var oldGeneration));

        extension.Service = new DisposalProbe();
        overlay.BuildProvider(extension.Id, extension, CreateContext(), (_, error) => throw error);
        Assert.True(overlay.TryGetGeneration(extension.Id, extension, out var replacementGeneration));

        Assert.NotSame(oldGeneration, replacementGeneration);
        Assert.False(overlay.TryCreateLease(extension.Id, extension, oldGeneration, out _));
        Assert.False(overlay.TryCreateScope(extension.Id, extension, oldGeneration, out _));
        Assert.Null(overlay.GetProviderForEndpointBuild(extension.Id, extension, oldGeneration));
        Assert.True(overlay.TryCreateLease(extension.Id, extension, replacementGeneration, out var lease));
        lease.Dispose();
    }

    [Fact]
    public async Task Materialized_endpoint_models_do_not_consult_retired_build_provider()
    {
        var builder = WebApplication.CreateBuilder();
        var hostDescriptors = builder.Services.ToList();
        await using var app = builder.Build();
        using var overlay = new ExtensionServiceOverlay(app.Services, hostDescriptors, logger: null);
        var extension = new DisposableServiceExtension();
        overlay.BuildProvider(extension.Id, extension, CreateContext(), (_, error) => throw error);
        Assert.True(overlay.TryGetGeneration(extension.Id, extension, out var generation));
        var buildServices = Assert.IsAssignableFrom<IServiceProvider>(
            overlay.GetProviderForEndpointBuild(extension.Id, extension, generation));
        var source = new ExtensionEndpointDataSource(app, extension.Id, buildServices);
        source.MapGet("/extension-probe", (DisposalProbe _) => Results.Ok());

        var materialized = source.MaterializeEndpoints();

        extension.Service = new DisposalProbe();
        overlay.BuildProvider(extension.Id, extension, CreateContext(), (_, error) => throw error);

        Assert.Single(materialized);
        Assert.Same(materialized, source.Endpoints);
    }

    [Fact]
    public void Synchronous_scope_disposal_cleans_up_async_only_scoped_services()
    {
        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        using var root = hostServices.BuildServiceProvider();
        using var overlay = new ExtensionServiceOverlay(root, hostServices.ToList(), logger: null);
        var extension = new AsyncScopedServiceExtension();

        overlay.BuildProvider(extension.Id, extension, CreateContext(), (_, error) => throw error);
        var scope = overlay.CreateScope(extension.Id);
        var service = scope.ServiceProvider.GetRequiredService<AsyncDisposalProbe>();

        scope.Dispose();

        Assert.Equal(1, service.DisposeAsyncCount);
    }

    [Fact]
    public async Task Asynchronous_scope_disposal_cleans_up_async_only_scoped_services()
    {
        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        using var root = hostServices.BuildServiceProvider();
        using var overlay = new ExtensionServiceOverlay(root, hostServices.ToList(), logger: null);
        var extension = new AsyncScopedServiceExtension();

        overlay.BuildProvider(extension.Id, extension, CreateContext(), (_, error) => throw error);
        var scope = overlay.CreateScope(extension.Id);
        var service = scope.ServiceProvider.GetRequiredService<AsyncDisposalProbe>();

        await Assert.IsAssignableFrom<IAsyncDisposable>(scope).DisposeAsync();

        Assert.Equal(1, service.DisposeAsyncCount);
    }

    [Fact]
    public async Task Concurrent_scope_release_and_provider_retirement_dispose_each_generation_once()
    {
        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        using var root = hostServices.BuildServiceProvider();
        using var overlay = new ExtensionServiceOverlay(root, hostServices.ToList(), logger: null);
        var extension = new DisposableServiceExtension();

        var first = new DisposalProbe();
        extension.Service = first;
        overlay.BuildProvider(extension.Id, extension, CreateContext(), (_, error) => throw error);
        var activeScope = overlay.CreateScope(extension.Id);
        Assert.Same(first, activeScope.ServiceProvider.GetRequiredService<DisposalProbe>());

        var second = new DisposalProbe();
        extension.Service = second;
        using var start = new ManualResetEventSlim();
        var replace = Task.Run(() =>
        {
            start.Wait();
            overlay.BuildProvider(extension.Id, extension, CreateContext(), (_, error) => throw error);
        });
        var release = Task.Run(() =>
        {
            start.Wait();
            activeScope.Dispose();
        });

        start.Set();
        await Task.WhenAll(replace, release);

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, second.DisposeCount);
        using var currentScope = overlay.CreateScope(extension.Id);
        Assert.Same(second, currentScope.ServiceProvider.GetRequiredService<DisposalProbe>());
    }

    [Fact]
    public async Task Stopping_background_worker_waits_for_worker_and_scope_to_drain()
    {
        var manager = new ExtensionManager(CreateContext());
        var extension = new SlowStoppingWorkerExtension();
        manager.Register(extension);

        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        using var root = hostServices.BuildServiceProvider();
        manager.PrepareRuntimeServices(root);
        manager.StartBackgroundWorker(extension.Id);
        await extension.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = Task.Run(() => manager.StopBackgroundWorker(extension.Id));
        await extension.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(stop.IsCompleted);

        extension.Release.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(extension.Exited.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Event_execution_pins_services_captured_during_initialization_across_disable()
    {
        var manager = new ExtensionManager(CreateContext());
        var extension = new BlockingEventExtension();
        manager.Register(extension, "local");
        MarkAsRuntimeExtension(manager, extension.Id);

        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        manager.CaptureHostServices(hostServices);
        using var root = hostServices.BuildServiceProvider();
        manager.PrepareRuntimeServices(root);
        Assert.True(await manager.InitializeExtensionAsync(extension.Id, root));

        var first = extension.Service;
        var dispatch = manager.DispatchEventAsync(new ExtensionEvent("test", "tag", 1));
        await extension.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await manager.DisableExtensionAsync(extension.Id);

        Assert.Equal(0, first.DisposeCount);
        Assert.Same(first, extension.CapturedServices!.GetRequiredService<DisposalProbe>());
        Assert.Throws<InvalidOperationException>(() => manager.CreateExtensionScope(extension.Id));
        Assert.Equal(
            extension.Name,
            manager.ExecuteExtensionMetadata(extension, () => extension.Name));

        extension.Release.TrySetResult();
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, first.DisposeCount);
    }

    private static void MarkAsRuntimeExtension(ExtensionManager manager, string extensionId)
    {
        var field = typeof(ExtensionManager).GetField(
            "_overlayExtensionIds",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var ids = Assert.IsType<HashSet<string>>(field?.GetValue(manager));
        ids.Add(extensionId);
    }

    private static ExtensionContext CreateContext() => new()
    {
        Configuration = new ConfigurationBuilder().Build(),
        DataDirectory = Path.GetTempPath(),
        CoveVersion = "1.0.0",
    };

    private sealed class DisposableServiceExtension : IExtension
    {
        public string Id => "com.example.disposable-service";
        public string Name => "Disposable service";
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;
        public DisposalProbe Service { get; set; } = new();
        public bool ThrowOnConfigure { get; set; }

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
        {
            if (ThrowOnConfigure)
                throw new InvalidOperationException("Expected ConfigureServices failure.");

            services.AddSingleton(_ => Service);
            services.AddScoped<ScopeFactoryConsumer>();
        }
    }

    private sealed class AsyncDisposableServiceExtension : IExtension
    {
        public string Id => "com.example.async-disposable-service";
        public string Name => "Async disposable service";
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;
        public AsyncDisposalProbe Service { get; set; } = new();

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
            => services.AddSingleton(_ => Service);
    }

    private sealed class SlowStoppingWorkerExtension : IBackgroundExtension
    {
        public string Id => "com.example.slow-stopping-worker";
        public string Name => "Slow stopping worker";
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
        {
        }

        public async Task RunAsync(IServiceProvider services, CancellationToken ct)
        {
            using var throwingCallback = ct.Register(() => throw new InvalidOperationException("Expected cancellation callback failure."));
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                await Release.Task;
                throw;
            }
            finally
            {
                Exited.TrySetResult();
            }
        }
    }

    private sealed class AsyncScopedServiceExtension : IExtension
    {
        public string Id => "com.example.async-scoped-service";
        public string Name => "Async scoped service";
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
            => services.AddScoped<AsyncDisposalProbe>();
    }

    private sealed class BlockingEventExtension : IEventExtension
    {
        public string Id => "com.example.blocking-event";
        public string Name => "Blocking event";
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;
        public DisposalProbe Service { get; set; } = new();
        public IServiceProvider? CapturedServices { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
            => services.AddSingleton(_ => Service);

        public Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
        {
            CapturedServices = services;
            return Task.CompletedTask;
        }

        public async Task OnEventAsync(ExtensionEvent evt, CancellationToken ct = default)
        {
            Assert.Same(Service, CapturedServices!.GetRequiredService<DisposalProbe>());
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
    }

    private sealed class DisposalProbe : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class AsyncDisposalProbe : IAsyncDisposable
    {
        public int DisposeAsyncCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeAsyncCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScopeFactoryConsumer(IExtensionServiceScopeFactory scopeFactory)
    {
        public IExtensionServiceScopeFactory ScopeFactory { get; } = scopeFactory;
    }

}
