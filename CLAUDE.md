# Working in this repo

Start with [CONTRIBUTING.md](CONTRIBUTING.md) for setup, PR expectations, and the AI policy —
in particular, every change needs a human author who understands it and stands behind it.

## The extension ABI is `Cove.Core`, `Cove.Plugins`, `Cove.Sdk`

Extensions are separately compiled DLLs that ship on their own schedule and load into the running
host. Their IL is frozen at *their* build time, so the public surface of those three projects is a
binary contract with code you are not editing and cannot recompile.

**Before changing any public signature in `src/Cove.Core`, `src/Cove.Plugins`, or `src/Cove.Sdk`,
read the Compatibility / ABI section of [src/Cove.Sdk/README.md](src/Cove.Sdk/README.md).**

The trap worth naming here, because it looks safe and the compiler will not warn you:

> **Appending a parameter to an existing method breaks every shipped extension, even with a default
> value.** Optional parameters are resolved at the call site, so an already-compiled extension holds
> a hard reference to the old arity and throws `MissingMethodException` when that call is first
> reached — often deep inside a feature, long after startup.

Keep the old signature as a forwarding default interface method (`[EditorBrowsable(Never)]`, no
default values on the shim). The README has the pattern, the other breaking shapes — records gaining
positional parameters, new abstract interface members, renames, return-type changes — and how to
verify a shim reached metadata.

A green build proves nothing about this. In-repo call sites keep compiling precisely because the
change is source-compatible; only already-built extensions break.
