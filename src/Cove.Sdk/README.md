# Cove SDK — extension packaging & host contracts

How a Cove extension should reference the host and what it may ship. Getting this wrong produces
packages that are bloated and, in the worst case, silently broken — so the rules below are enforced
by tooling on both sides rather than left to each author.

## The one rule

For every assembly your extension references, decide: **do this library's types ever cross between
your extension and the host?**

- **Yes → host-provided (compile-only, never shipped).** The host owns the single loaded copy; you
  compile against it but must not put it in your package.
- **No → extension-private (shipped).** It is internal to your extension; ship it so it travels with
  the plugin. Two extensions can even ship different versions — they stay isolated.

### Why shipping a host assembly is dangerous

Each extension loads into its own `AssemblyLoadContext` (ALC). A .NET `Type`'s identity is
**(assembly identity) + (the ALC that loaded it)**, not its bytes. If your package ships, say,
`Cove.Core.dll` and it were ever loaded into your ALC, there would be two `Cove.Core` assemblies in
the process — and `Cove.Core.Entities.Embedding` from the host ALC and from your ALC would be
**different types**. Casts throw `InvalidCastException`, DI lookups miss, and the Npgsql `pgvector`
handler (bound to the host's `Pgvector.Vector`) rejects "your" `Vector`. Everything compiles; it
breaks at runtime, intermittently, only under version skew. So we never ship them.

### Which bucket common dependencies fall in

| Dependency | Crosses host boundary? | Bucket |
|---|---|---|
| `Cove.Sdk`, `Cove.Core`, `Cove.Plugins` | yes (entities, DTOs, interfaces, base classes) | host-provided |
| EF Core, `Npgsql`, `Pgvector` | yes (you call the host `DbContext`, pass `Vector`) | host-provided |
| ONNX Runtime, tokenizers, image codecs, etc. | no (used internally to produce bytes/vectors) | extension-private |

## How it's enforced (you mostly don't think about it)

1. **`Cove.Sdk` ships the packaging rules.** Referencing the `Cove.Sdk` NuGet package pulls in
   `buildTransitive/Cove.Sdk.targets`, which turns on plugin output (`EnableDynamicLoading`) and strips
   the host-provided closure (`CoveHostProvidedAssemblies`) from your build and publish output. A
   third-party extension that just references `Cove.Sdk` gets correct packaging for free.

2. **The host ignores bundled host assemblies anyway.** `ExtensionLoadContext` always resolves the
   host's copy of any assembly in the host closure (even one bundled by mistake), and logs a one-time
   warning naming the offending assembly so you can slim the package. Correctness does not depend on
   every author packaging perfectly.

If you maintain extensions in this repo, the same rules are applied centrally by
`Directory.Build.targets`, and the host contracts are referenced for you — your `.csproj` only declares
the extension's own private dependencies.

### Adding a dependency

- **Private dependency** (the normal case): add a `<PackageReference>` to your extension `.csproj`. It
  ships automatically.
- **A new host-provided dependency** (rare — only when the host starts providing a new shared library):
  add its assembly simple name to `CoveHostProvidedAssemblies` (in `Cove.Sdk.targets` for the
  ecosystem, and in this repo's `Directory.Build.targets`).

## Compatibility / ABI

The `Cove.Core` / `Cove.Plugins` / `Cove.Sdk` trio **is** the extension ABI. Extensions compile against
a version of it and declare `minCoveVersion` in `extension.json`; the host refuses to load an extension
that needs a newer host than is running. Keep the trio's public surface the stable contract — treat a
breaking change to it as a major version of the extension ABI.

`minCoveVersion` only stops an extension built for a *newer* host. It does nothing for the common
case: an extension built against an older host that the running host has since broken. That
extension loads happily and then throws `MissingMethodException` the moment the changed method is
first JIT'd — usually deep in a feature, long after startup, so nothing in the extension list looks
wrong.

### Adding a parameter is a breaking change, default value or not

Optional parameters are compile-time sugar. The compiler bakes the argument list into the caller's
IL at the call site, so an already-compiled extension holds a hard reference to the *old arity*.
Appending `= null` to a new parameter keeps every in-repo caller compiling and silently breaks every
shipped extension.

This is not hypothetical: Cove 1.4 appended `FilterExpression<T>? expression = null` to
`IVideoRepository.FindAsync`/`AggregateAsync` and `IPerformerRepository.FindAsync`, and
`IReadOnlyList<string>? paths = null` to `ICleanService.StartClean`. Every extension built against
1.3 lost those calls.

When you must change a signature on the trio's public surface, keep the old one as a forwarding
default interface method so implementers need no changes:

```csharp
Task<(IReadOnlyList<Video> Items, int TotalCount)> FindAsync(
    VideoFilter? filter, FindFilter? findFilter, CancellationToken ct = default,
    FilterExpression<VideoFilter>? expression = null);

// Binary-compatibility shim for extensions compiled before `expression` was appended.
[EditorBrowsable(EditorBrowsableState.Never)]
Task<(IReadOnlyList<Video> Items, int TotalCount)> FindAsync(
    VideoFilter? filter, FindFilter? findFilter, CancellationToken ct)
    => FindAsync(filter, findFilter, ct, null);
```

Declare the shim **without** default values. A three-argument call then binds to it exactly, while a
two-argument call still resolves to the modern overload — no ambiguity either way.
`[EditorBrowsable(Never)]` hides it from extension authors' IntelliSense so no new code binds to it.

### The other shapes that break already-built extensions

- **Adding a positional parameter to a public `record`** — the primary constructor and `Deconstruct`
  both change arity. Reading properties stays fine; constructing or deconstructing breaks. Add an
  explicit constructor at the old arity.
- **Changing a parameter type** — shimmable, by keeping an overload that takes the old type and
  converting. **Changing a return type is not**: C# cannot overload on return type alone, so the old
  shape has to survive under a different method name.
- **Adding an abstract member to an interface extensions implement** (`IExtension`, `IUIExtension`,
  and friends) — give it a default implementation, or every existing extension fails to load.
- **Renaming a public type or member, or moving it between namespaces or assemblies.**

Verify a shim actually landed in metadata rather than trusting that it compiled — a clean build
proves nothing here, since the source-level call sites were never broken:

```csharp
typeof(IVideoRepository).GetMethods()
    .Where(m => m.Name == "FindAsync")
    .Select(m => string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)))
```

## Authentication assertions

Authentication extensions hand Cove a stable provider-owned identity with
`TrySetExtensionIdentityAssertion`. The extension ID, provider ID, and exact subject identify the
link; account and provider labels are display metadata only. Interactive sign-in assertions leave
`ExtensionIdentityAssertion.IsAuthoritative` false and become a Cove session only through the
browser-bound login-ticket flow.

Set `IsAuthoritative` only when a trusted upstream authenticates every request, such as a reverse
proxy that strips client-supplied identity headers and supplies a verified stable subject. An
authoritative assertion replaces a stale Cove user session that belongs to a different user and
fails closed when its identity is unlinked or unusable. A same-user bearer principal keeps its token
scope, and an explicit share-link principal keeps share-link scope.

Every active Cove user retains a local password. External identity links are optional alternative
sign-in methods, so unlinking an identity or disabling its extension never removes the user's local
login path. Authentication extensions must not provision users, remove passwords, or treat an
external provider as the sole account-recovery mechanism.

## Invalidating segment-span caches

Extensions that commit changes to Cove's video segments outside the built-in controllers must resolve
`Cove.Core.Interfaces.ISegmentSpanCacheInvalidator` from their service provider. Call
`InvalidateVideo(videoId)` after the transaction commits. Cove then removes raw-segment, resolved-span,
and derived-query projections for that video, including results that were still being computed when
the invalidation happened.

`InvalidateAll()` is available for bulk operations that cannot identify the affected videos, but
video-specific invalidation should be preferred. The service only invalidates projections; it neither
persists nor authorizes the underlying mutation.

## Host services and extension-container ownership

Each runtime extension has a reloadable service container. Closed host singletons available through
the SDK contract are forwarded as the exact host-owned instance; disabling or upgrading an extension
does not dispose them. Scoped and transient host registrations are recreated inside the extension
scope, while extension registrations remain extension-owned and are disposed when that provider
generation drains.

Cove deliberately does not copy arbitrary open-generic host singleton registrations. The built-in
container cannot forward future closed instances while preserving host ownership, and copying the
descriptor would let extension reload dispose services the host still owns. Logging, options, and
HTTP client generics are rebuilt by Cove. An extension that needs another open-generic service must
register and own its implementation in `ConfigureServices`.
