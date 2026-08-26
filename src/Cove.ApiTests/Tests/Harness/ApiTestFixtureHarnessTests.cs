using Cove.ApiTests.Infrastructure;
using System.Reflection;

namespace Cove.ApiTests.Tests.Harness;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SelfHostedApiTestCollection
{
    public const string Name = "Self-hosted API tests";
}

[Collection(SelfHostedApiTestCollection.Name)]
public sealed class ApiTestFixtureHarnessTests(CoveApiTestPool assemblyPool)
{
    [Fact]
    public async Task GivenPooledApiTestClasses_WhenLeasesRunConcurrently_ThenReuseRetirementAndDisposalAreExact()
    {
        var assembly = typeof(ApiTest).Assembly;
        assembly.GetCustomAttributes<AssemblyFixtureAttribute>()
            .Select(attribute => attribute.AssemblyFixtureType)
            .Should().Contain(typeof(CoveApiTestPool));
        var collectionBehavior = assembly.GetCustomAttribute<CollectionBehaviorAttribute>();
        collectionBehavior.Should().NotBeNull();
        collectionBehavior!.MaxParallelThreads.Should().Be(CoveApiTestPool.MaxParallelThreads);
        collectionBehavior.ParallelAlgorithm.Should().Be(Xunit.Sdk.ParallelAlgorithm.Conservative);
        assemblyPool.ConfiguredCapacity.Should().Be(CoveApiTestPool.MaxParallelThreads);
        assemblyPool.IsInitialized.Should().BeTrue();
        typeof(ApiTest).Should().BeAssignableTo<IClassFixture<CoveApiTestFixture>>();
        var apiTestTypes = assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(ApiTest).IsAssignableFrom(type))
            .ToArray();
        apiTestTypes.Should().NotBeEmpty();
        foreach (var apiTestType in apiTestTypes)
            apiTestType.GetCustomAttributes<CollectionAttribute>(inherit: false).Should().BeEmpty();

        var pool = new CoveApiTestPool(capacity: 2);
        var fixtures = new[]
        {
            new CoveApiTestFixture(pool),
            new CoveApiTestFixture(pool),
            new CoveApiTestFixture(pool),
            new CoveApiTestFixture(pool),
            new CoveApiTestFixture(pool),
        };
        string[] dataRoots = [];
        string[] databaseNames = [];

        try
        {
            var poolInitialization = pool.InitializeAsync().AsTask();
            Func<Task> duplicateInitialization = () => pool.InitializeAsync().AsTask();
            await duplicateInitialization.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*already been initialized*");
            await poolInitialization;
            await Task.WhenAll(fixtures.Take(2).Select(fixture => fixture.InitializeAsync().AsTask()));

            pool.ConfiguredCapacity.Should().Be(2);
            fixtures.Take(2).Should().OnlyContain(fixture => fixture.IsInitialized);
            fixtures[2].IsInitialized.Should().BeFalse();
            fixtures[0].BaseAddress.Should().NotBe(fixtures[1].BaseAddress);
            fixtures[0].DatabaseName.Should().NotBe(fixtures[1].DatabaseName);
            fixtures[0].DataRoot.Should().NotBe(fixtures[1].DataRoot);
            fixtures[0].ProcessStartedTimestamp.Should().BeLessThan(
                fixtures[1].ReadyTimestamp,
                "the first API host process should start before the second host becomes ready");
            fixtures[1].ProcessStartedTimestamp.Should().BeLessThan(
                fixtures[0].ReadyTimestamp,
                "the second API host process should start before the first host becomes ready");

            dataRoots = fixtures.Take(2).Select(fixture => fixture.DataRoot).ToArray();
            databaseNames = fixtures.Take(2).Select(fixture => fixture.DatabaseName).ToArray();
            dataRoots.Should().OnlyContain(dataRoot => Directory.Exists(dataRoot));
            foreach (var databaseName in databaseNames)
                (await PostgreSqlTestDatabase.ExistsAsync(databaseName, TestContext.Current.CancellationToken)).Should().BeTrue();

            var returnedAddress = fixtures[0].BaseAddress;
            var returnedDatabase = fixtures[0].DatabaseName;
            var waitingInitialization = fixtures[2].InitializeAsync().AsTask();
            waitingInitialization.IsCompleted.Should().BeFalse();
            await fixtures[0].DisposeAsync();
            await waitingInitialization;

            fixtures[0].IsInitialized.Should().BeFalse();
            fixtures[2].BaseAddress.Should().Be(returnedAddress);
            fixtures[2].DatabaseName.Should().Be(returnedDatabase);

            var retiredAddress = fixtures[2].BaseAddress;
            var retiredDatabase = fixtures[2].DatabaseName;
            var retiredDataRoot = fixtures[2].DataRoot;
            var retainedAddress = fixtures[1].BaseAddress;
            var retainedDatabase = fixtures[1].DatabaseName;
            var retainedDataRoot = fixtures[1].DataRoot;
            fixtures[2].RetireAfterClass();
            await Task.WhenAll(fixtures.Skip(1).Take(2).Select(fixture => fixture.DisposeAsync().AsTask()));

            Directory.Exists(retiredDataRoot).Should().BeFalse();
            (await PostgreSqlTestDatabase.ExistsAsync(retiredDatabase, TestContext.Current.CancellationToken)).Should().BeFalse();

            await Task.WhenAll(fixtures.Skip(3).Select(fixture => fixture.InitializeAsync().AsTask()));
            fixtures.Skip(3).Should().OnlyContain(fixture => fixture.IsInitialized);
            fixtures[3].BaseAddress.Should().NotBe(fixtures[4].BaseAddress);
            fixtures.Skip(3).Should().OnlyContain(fixture => fixture.BaseAddress != retiredAddress);
            fixtures.Skip(3).Should().OnlyContain(fixture => fixture.DatabaseName != retiredDatabase);
            fixtures.Skip(3).Select(fixture => fixture.BaseAddress).Should().Contain(retainedAddress);
            fixtures.Skip(3).Select(fixture => fixture.DatabaseName).Should().Contain(retainedDatabase);
            fixtures.Skip(3).Select(fixture => fixture.DataRoot).Should().Contain(retainedDataRoot);
            dataRoots = fixtures.Skip(3).Select(fixture => fixture.DataRoot).ToArray();
            databaseNames = fixtures.Skip(3).Select(fixture => fixture.DatabaseName).ToArray();
            dataRoots.Should().OnlyContain(dataRoot => Directory.Exists(dataRoot));
            foreach (var databaseName in databaseNames)
                (await PostgreSqlTestDatabase.ExistsAsync(databaseName, TestContext.Current.CancellationToken)).Should().BeTrue();

            await Task.WhenAll(fixtures.Skip(3).Select(fixture => fixture.DisposeAsync().AsTask()));
            await pool.DisposeAsync();

            pool.IsInitialized.Should().BeFalse();
            fixtures.Should().OnlyContain(fixture => !fixture.IsInitialized);
            dataRoots.Should().OnlyContain(dataRoot => !Directory.Exists(dataRoot));
            foreach (var databaseName in databaseNames)
                (await PostgreSqlTestDatabase.ExistsAsync(databaseName, TestContext.Current.CancellationToken)).Should().BeFalse();
        }
        finally
        {
            await DisposeFixturesAndPoolAsync(fixtures, pool);
        }
    }

    private static async Task DisposeFixturesAndPoolAsync(
        IEnumerable<CoveApiTestFixture> fixtures,
        CoveApiTestPool pool)
    {
        var errors = new List<Exception>();
        try
        {
            await Task.WhenAll(fixtures.Select(fixture => fixture.DisposeAsync().AsTask()));
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        try
        {
            await pool.DisposeAsync();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        if (errors.Count == 1)
            throw errors[0];
        if (errors.Count > 1)
            throw new AggregateException("The API test harness could not dispose all fixtures and pool resources.", errors);
    }
}
