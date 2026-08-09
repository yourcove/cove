# Stash video-cover migration test plan

This plan verifies durable Stash scene-cover migration, preservation of
generated video assets during rescans, and the offline repair utility for
libraries imported before the fix. Store commands, reports, database
inventories, file hashes, screenshots, and fixture identities privately under
`gitignored/dev/stash-cover-migration/`; do not add completed-run notes here.

## Safety and isolation

- Load `gitignored/dev/agent.env` before every command.
- Use the immutable Stash fixture read-only. Create named private writable
  copies only for destructive source-fixture cases.
- Give every scenario a fresh PostgreSQL database and generated-data
  directory. Never reuse a partial or failed target unless the scenario
  explicitly proves that reuse is safe.
- Before every utility invocation, confirm Cove is stopped with no connection
  to the target, the database URL and generated path name the intended target,
  and the Stash database is the immutable fixture or named private copy.
- Review and preserve the dry-run JSON report before apply. The utility is the
  supported offline repair path; do not run apply while Cove is live.
- Preserve full private evidence and keep domains, credentials, entity IDs,
  checksums, paths, library names, and titles out of tracked files.
- Use the UI first for live Cove behavior. Treat API and SQL checks as
  supporting evidence.

## Cohort and inventories

Select a small deterministic cohort covering:

- unique JPEG, PNG, and WebP explicit covers;
- no explicit cover;
- a cover shared across entities;
- marker data and multiple video files;
- a user-assigned Cove cover override;
- missing and ambiguous repair matches.

Capture database inventories and SHA-256 manifests before and after each
mutation. Include cover references, referenced blob payloads, screenshots,
previews, sprites, VTTs, marker relationships, multiple-file relationships,
and unrelated sentinel state. Exercise successful FFmpeg generation, failure,
and cancellation on separate disposable files. Successful output should be
published atomically; failure must preserve prior assets. Cancellation must
preserve each published file as valid, although already-committed replacements
may remain until a later successful job converges the asset set.

## Scenario matrix

### 1. Fresh fixed migration

Import into an empty fixed-version target, capture the post-migration
inventory, run an unchanged forced rescan of the cohort, and exercise FFmpeg
success, failure, and cancellation.

Verify explicit Stash covers become durable `videos.ImageBlobId` references,
are not duplicated into the screenshot cache, and remain unchanged during an
unchanged rescan. Only persistently referenced Stash blobs should be imported;
the no-cover scene may use the generated screenshot fallback. Marker and
multiple-file relationships must remain intact. When two imported entities
share a blob, replacing or deleting one image must not remove the payload still
referenced by the other entity.

### 2. Legacy migration, upgrade without repair

Import on the pre-fix baseline, capture the legacy state, upgrade without
backfill, and run the unchanged cohort rescan.

Verify upgrade is non-destructive but does not infer missing legacy cover
references. Legacy screenshots and blob/screenshot duplication remain, and
unchanged rescanning keeps existing assets usable.

### 3. Legacy migration, repair before upgrade

Import on the pre-fix baseline, assign the Cove cover override, stop Cove,
review and apply the repair, prove idempotency, upgrade, and run the selective
rescan matrix.

Verify only null or empty references with unique fingerprint and blob-content
matches change. Preserve the override, missing or ambiguous mappings, every
generated file, and all unrelated state. Converted-image cases must resolve
from source video fingerprints and source blob checksums rather than screenshot
equality.

### 4. Legacy migration, upgrade before repair

Repeat Scenario 3 with upgrade before repair. Verify deployment order does not
change the safe plan or final references.

### 5. Partially damaged legacy state

On a private legacy target, replace or remove selected generated screenshots
through controlled pre-fix rescans, then upgrade, repair while Cove is stopped,
and run the fixed selective rescan matrix.

Verify repair does not depend on a legacy screenshot still existing or
matching the source cover. Unique source mappings are repaired; missing or
ambiguous payloads remain unchanged and are reported.

### 6. Repair failure and race safety

Use separate disposable targets and private fixture copies to verify:

- dry-run changes no database row or generated file;
- a cover assigned after planning causes guarded apply to abort;
- invalid credentials, a missing generated directory, and incompatible Stash
  schema fail before mutation;
- interrupting an apply during payload hashing changes nothing;
- a missing payload is reported and skipped without a dangling reference;
- duplicate matching Cove payloads are ambiguous and skipped;
- misplaced payloads and blob IDs with multiple physical files are
  disqualified even when one file has matching content;
- a second apply after success changes zero rows.

After every failure, compare all cover references, blob rows and payloads,
generated assets, fingerprints, and sentinel state with the pre-run inventory.

## Backfill commands

Dry-run:

```bash
source gitignored/dev/agent.env
node scripts/backfill-stash-video-covers.mjs \
  --stash-db /path/to/stash-go.sqlite \
  --generated-path /path/to/cove/generated \
  --report /private/path/stash-cover-backfill-dry-run.json
```

Apply only after stopping Cove, confirming the target bindings, and reviewing
the saved dry-run report:

```bash
source gitignored/dev/agent.env
node scripts/backfill-stash-video-covers.mjs \
  --stash-db /path/to/stash-go.sqlite \
  --generated-path /path/to/cove/generated \
  --apply \
  --report /private/path/stash-cover-backfill-applied.json
```

The utility updates database references only. It does not delete legacy
screenshots, blob payloads, or other generated assets.
