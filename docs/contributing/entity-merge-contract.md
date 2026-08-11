# Entity merge contract

Cove's tag, performer, and studio merge services are the authoritative implementations for transferring relationships when an entity is merged. New Cove-owned references to these entities must be added to the corresponding merge service and its regression tests.

Every merge runs transactionally. The requested target and sources are first resolved through the caller's normal visibility and authorization rules. Once authorized, the transfer sees Cove-owned references hidden by row-level content filters so another user's engagement or relationships to restricted content are not lost.

Set-like relationships are unioned and duplicate rows are removed. The survivor keeps populated intrinsic metadata and fills empty fields from sources in deterministic ID order. Boolean favorite and organized state is combined with logical OR. Hierarchy references are remapped on both ends, duplicate edges are removed, and self-edges introduced by the merge are discarded.

The services transfer direct media relationships, aliases, URLs and remote identifiers, segments, groups, custom fields, provenance, ratings, bookmarks, affinities, interactions, playback history, security rules, saved and default filters, dynamic queries, share links, and documented Cove-owned JSON references. Provider- or extension-defined opaque JSON is not rewritten because Cove cannot safely infer its identifier semantics.

Tag aliases created from merged canonical names are trimmed and omitted when blank, equal to the survivor's canonical name, or already present. Performer merges also repoint linked faces and invalidate source-specific materialized face suggestions for recomputation. Studio merges remap parent relationships without introducing cycles.

Artwork changes use Cove's blob-reference transaction coordinator. Assignments are validated inside the database transaction, detached blob identifiers are accumulated, and payload cleanup happens only after commit. Rollback discards the cleanup plan.

Before deleting a source, Cove inventories PostgreSQL foreign keys outside the known core transfer contract, including tables left by disabled extensions. Ordinary merges fail closed when such references exist or cannot be inspected because of permissions or row-level security. The generic inspector and repair primitives remain available for future extension-aware merge workflows, but Cove does not infer or modify opaque non-foreign-key extension data.
