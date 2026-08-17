using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cove.Tests.Integration;

/// <summary>
/// Pins the JSON wire contract for enum values on the surfaces governed by
/// <see cref="Microsoft.AspNetCore.Http.Json.JsonOptions"/>: minimal-API endpoints, extension
/// endpoints, and the schema document the OpenAPI generator produces.
///
/// <para><b>Group A</b> resolves options off a host that boots the real <c>Program.cs</c>, so it
/// proves that <c>Program.cs</c> itself made the registration, and that the MVC and SignalR options
/// are untouched and are distinct objects.</para>
///
/// <para><b>Group B</b> drives real HTTP round-trips through a hand-built minimal-API host whose two
/// endpoints carry the extension marker, so they are shaped exactly like an extension endpoint. Group
/// B proves that such an endpoint serializes and binds through the HTTP JSON options path over a real
/// pipeline. It does <b>not</b> prove that <c>Program.cs</c> registered the converter — Group A does
/// that — and it does <b>not</b> exercise an endpoint published by a genuinely registered extension.
/// Publishing one in-tree would need <c>InitializeExtensionAsync</c> plus a database reset, and is out
/// of scope here; the runtime-DLL overlay path is reasoned about elsewhere and is not claimed proven.
/// </para>
///
/// <para>Every output assertion reads raw response text or a <c>JsonElement</c> parsed with default
/// options. Nothing here goes through a helper that adds a camel-case enum converter client-side: such
/// a helper deserializes both the numeric and the string form to the same enum value, so an assertion
/// made through it passes identically with and without the registration under test.</para>
///
/// <para>The generated-schema assertion below checks the member array and its casing only. That is
/// what the live capture recorded: <c>JsonSchemaExporter</c> emits the member array and omits any
/// keyword naming the JSON type for converter-backed enums (dotnet/aspnetcore#61303, closed as not
/// planned), so an assertion about such a keyword would claim a shape the document does not have.</para>
/// </summary>
public sealed class JsonEnumWireContractTests
{
    private static readonly string[] ExpectedAiRunStatusMembers =
        ["cancelled", "completed", "failed", "pending", "running"];

    // ---------------------------------------------------------------------------------------------
    // Group A - against the real Program.cs
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Program_registers_exactly_one_string_enum_converter_on_the_http_json_options()
    {
        using var factory = new CoveWebApplicationFactory();

        var converters = HostHttpJsonOptions(factory).Converters.OfType<JsonStringEnumConverter>().ToArray();

        // Exactly one, not merely at least one: a second ConfigureHttpJsonOptions call added later
        // must show up here as a change rather than pass as silently harmless.
        Assert.Single(converters);
    }

    [Fact]
    public void An_enum_member_serializes_as_its_camel_case_name_through_the_host_options()
    {
        using var factory = new CoveWebApplicationFactory();
        var options = HostHttpJsonOptions(factory);

        var json = JsonSerializer.Serialize(new StatusPayload(AiRunStatus.Running), options);

        Assert.Contains("\"status\":\"running\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"status\":2", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"status":1}""", AiRunStatus.Pending)]
    [InlineData("""{"status":5}""", AiRunStatus.Cancelled)]
    public void The_numeric_form_still_deserializes_after_the_change(string body, AiRunStatus expected)
    {
        // AiRunStatus members start at 1 (Pending = 1 .. Cancelled = 5), so the lowest and highest
        // defined members are 1 and 5. Sending the undefined 0 would prove nothing.
        using var factory = new CoveWebApplicationFactory();
        var options = HostHttpJsonOptions(factory);

        var payload = JsonSerializer.Deserialize<StatusPayload>(body, options);

        Assert.NotNull(payload);
        Assert.Equal(expected, payload.Status);
    }

    [Fact]
    public void An_integer_outside_the_defined_members_behaves_exactly_as_it_did_without_the_converter()
    {
        using var factory = new CoveWebApplicationFactory();

        // The unconverted baseline is what the host's options were before the registration. Comparing
        // the two outcomes in one test is a real before/after comparison, and it asserts agreement
        // rather than a guessed outcome.
        var throughHost = Outcome(HostHttpJsonOptions(factory));
        var throughUnconvertedWebDefaults = Outcome(new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(throughUnconvertedWebDefaults, throughHost, StringComparer.Ordinal);

        static string Outcome(JsonSerializerOptions options)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<StatusPayload>("""{"status":99}""", options);
                return $"value:{(int)payload!.Status}";
            }
            catch (Exception ex)
            {
                return $"throws:{ex.GetType().FullName}";
            }
        }
    }

    [Fact]
    public void A_null_nullable_enum_member_serializes_as_json_null_and_round_trips_back_to_null()
    {
        using var factory = new CoveWebApplicationFactory();
        var options = HostHttpJsonOptions(factory);

        var json = JsonSerializer.Serialize(new NullableStatusPayload(null), options);

        Assert.Contains("\"status\":null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"null\"", json, StringComparison.Ordinal);

        // ValueKind rules out both the quoted word and the number in one assertion.
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("status").ValueKind);

        var back = JsonSerializer.Deserialize<NullableStatusPayload>("""{"status":null}""", options);
        Assert.NotNull(back);
        Assert.Null(back.Status);
    }

    [Fact]
    public void Serializing_deserializing_and_serializing_again_yields_an_identical_string()
    {
        using var factory = new CoveWebApplicationFactory();
        var options = HostHttpJsonOptions(factory);

        var first = JsonSerializer.Serialize(new StatusPayload(AiRunStatus.Cancelled), options);
        var middle = JsonSerializer.Deserialize<StatusPayload>(first, options);
        var second = JsonSerializer.Serialize(middle, options);

        Assert.Equal(first, second, StringComparer.Ordinal);
    }

    [Fact]
    public void Mvc_still_carries_its_own_converter_on_its_own_options_instance()
    {
        using var factory = new CoveWebApplicationFactory();

        var mvc = MvcJsonOptions(factory);
        var http = HostHttpJsonOptions(factory);

        Assert.Single(mvc.Converters.OfType<JsonStringEnumConverter>());
        Assert.Contains(
            "\"status\":\"running\"",
            JsonSerializer.Serialize(new StatusPayload(AiRunStatus.Running), mvc),
            StringComparison.Ordinal);

        // MVC's configuration is a distinct instance, not an alias of the HTTP JSON options.
        Assert.False(ReferenceEquals(mvc, http));
    }

    [Fact]
    public void SignalR_still_carries_its_own_converter_on_its_payload_options()
    {
        using var factory = new CoveWebApplicationFactory();

        var payloadOptions = SignalRPayloadOptions(factory);

        Assert.Single(payloadOptions.Converters.OfType<JsonStringEnumConverter>());
        Assert.Contains(
            "\"status\":\"running\"",
            JsonSerializer.Serialize(new StatusPayload(AiRunStatus.Running), payloadOptions),
            StringComparison.Ordinal);
    }

    [Fact]
    public void All_three_pipelines_spell_the_same_enum_member_identically()
    {
        using var factory = new CoveWebApplicationFactory();
        var payload = new StatusPayload(AiRunStatus.Running);

        var throughMvc = JsonSerializer.Serialize(payload, MvcJsonOptions(factory));
        var throughHttp = JsonSerializer.Serialize(payload, HostHttpJsonOptions(factory));
        var throughHub = JsonSerializer.Serialize(payload, SignalRPayloadOptions(factory));

        // The point of the change, asserted directly rather than one pipeline at a time: a client
        // reading an enum does not need to know which pipeline answered it.
        Assert.Equal("""{"status":"running"}""", throughMvc, StringComparer.Ordinal);
        Assert.Equal(throughMvc, throughHttp, StringComparer.Ordinal);
        Assert.Equal(throughMvc, throughHub, StringComparer.Ordinal);
    }

    [Fact]
    public void The_stored_json_options_deliberately_do_not_carry_the_wire_convention()
    {
        // CoveJson.Default is not a fourth pipeline. Its only callers are the metadata export and
        // import paths, so its shape is a file format: an export written with the enum converter names
        // members as strings, and a Cove predating this change refuses to read that file back. The
        // exported graph really does carry enums - Performer.Gender, Performer.Circumcised, Group.Kind
        // and GroupItem.Kind - so this is a live concern rather than a theoretical one. Pinned here so
        // the export format can only ever move as its own deliberate, released decision.
        Assert.Empty(CoveJson.Default.Converters.OfType<JsonStringEnumConverter>());

        var json = JsonSerializer.Serialize(new StatusPayload(AiRunStatus.Running), CoveJson.Default);

        using var document = JsonDocument.Parse(json);
        var status = document.RootElement.GetProperty("status");
        Assert.Equal(JsonValueKind.Number, status.ValueKind);
        Assert.Equal((int)AiRunStatus.Running, status.GetInt32());
    }

    [Fact]
    public void The_exporter_the_openapi_generator_uses_names_the_camel_case_members()
    {
        using var factory = new CoveWebApplicationFactory();

        // Proxy, stated as such: this is the identical API OpenApiSchemaService invokes, handed the
        // same options object, but it is not the generated document. The document itself is covered by
        // the live capture, because MapOpenApi() is Development-gated and no in-tree rig runs there.
        var node = System.Text.Json.Schema.JsonSchemaExporter.GetJsonSchemaAsNode(
            HostHttpJsonOptions(factory),
            typeof(AiRunStatus));

        var members = node!.AsObject()["enum"]!.AsArray()
            .Select(member => member!.GetValue<string>())
            .OrderBy(member => member, StringComparer.Ordinal)
            .ToArray();

        // Set comparison, never index-by-index: the exporter promises no member ordering, and the
        // declaration order in the C# enum is not part of any published contract.
        Assert.Equal(ExpectedAiRunStatusMembers, members);
    }

    [Fact]
    public async Task An_mvc_controller_response_still_emits_camel_case_enum_members_over_http()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/api/System/config");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.IsSuccessStatusCode,
            $"GET /api/System/config returned {(int)response.StatusCode}: {body}");

        // The rating-system pair carries a seeded default, so it needs no rows. Asserted on the raw
        // body, never through a typed read.
        Assert.Contains(":\"stars\"", body, StringComparison.Ordinal);
        Assert.Contains("\"starPrecision\":\"full\"", body, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Group B - real HTTP round-trips on a marker-carrying minimal-API endpoint
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_marker_carrying_minimal_api_endpoint_emits_the_camel_case_member_over_http()
    {
        await using var app = BuildProbeHost();
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/probe/status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        Assert.Contains("\"status\":\"running\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_marker_carrying_minimal_api_endpoint_still_binds_a_bare_integer_request_body()
    {
        await using var app = BuildProbeHost();
        await app.StartAsync();
        using var client = app.GetTestClient();

        // Posted as a raw literal, not through a serializer that would rewrite the integer: the whole
        // point is that the literal integer reaches the binder.
        using var content = new StringContent("""{"status":1}""", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/probe/status", content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");

        // "bound" is the member name the endpoint saw, independent of how it is serialized back;
        // "echoed" is the same member on its way out through the configured converter.
        Assert.Contains("\"bound\":\"Pending\"", body, StringComparison.Ordinal);
        Assert.Contains("\"echoed\":\"pending\"", body, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------

    private static JsonSerializerOptions HostHttpJsonOptions(CoveWebApplicationFactory factory)
        => factory.Services
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            .Value.SerializerOptions;

    private static JsonSerializerOptions MvcJsonOptions(CoveWebApplicationFactory factory)
        => factory.Services
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()
            .Value.JsonSerializerOptions;

    private static JsonSerializerOptions SignalRPayloadOptions(CoveWebApplicationFactory factory)
        => factory.Services
            .GetRequiredService<IOptions<Microsoft.AspNetCore.SignalR.JsonHubProtocolOptions>>()
            .Value.PayloadSerializerOptions;

    private static WebApplication BuildProbeHost()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        var app = builder.Build();
        app.MapGet("/probe/status", () => Results.Ok(new StatusPayload(AiRunStatus.Running)))
            .WithMetadata(new ExtensionEndpointMetadata("example.probe"));
        app.MapPost("/probe/status", (StatusPayload payload) => Results.Ok(new ProbeEcho(
                Bound: payload.Status.ToString(),
                Echoed: payload.Status)))
            .WithMetadata(new ExtensionEndpointMetadata("example.probe"));
        return app;
    }

    private sealed record StatusPayload(AiRunStatus Status);

    private sealed record NullableStatusPayload(AiRunStatus? Status);

    private sealed record ProbeEcho(string Bound, AiRunStatus Echoed);
}
