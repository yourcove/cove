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

## Invalidating segment-span caches

Extensions that commit changes to Cove's video segments outside the built-in controllers must resolve
`Cove.Core.Interfaces.ISegmentSpanCacheInvalidator` from their service provider. Call
`InvalidateVideo(videoId)` after the transaction commits. Cove then removes raw-segment, resolved-span,
and derived-query projections for that video, including results that were still being computed when
the invalidation happened.

`InvalidateAll()` is available for bulk operations that cannot identify the affected videos, but
video-specific invalidation should be preferred. The service only invalidates projections; it neither
persists nor authorizes the underlying mutation.
