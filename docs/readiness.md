# Readiness Checklist

This document records the original readiness questions and the measured
answers that shaped the implementation. The Compose stack now implements and
has exercised the critical extraction, publication, fallback, multi-instance,
rate-limit, and backup paths. Infrastructure-specific TLS, public hostname,
CDN policy, and Kubernetes ownership remain deployment decisions.

## Findings so far

The following snapshot was taken on 2026-08-19:

- Windows and macOS Realm app-init responses reported the same Realm build hash.
- Their `resources.assets` files had different checksums and sizes, so the
  service must deliberately select a canonical source platform.
- The current Windows `resources.assets.gz` transfer was approximately 47 MB,
  while the verified decompressed `resources.assets` file was approximately
  394 MB. Download and extraction limits must treat these sizes separately.
- The generic `net8.0` extractor target compiled with zero errors on Windows.
- A NuGet vulnerability scan reported no known vulnerable direct or transitive
  packages from the configured sources.
- The extractor build currently emits 153 compiler warnings, primarily
  nullability and hidden-member warnings. They do not block the proof of
  concept, but the production code paths should be audited separately.
- The accepted EAM cache contains 14,655 renderable IDs across multiple object
  categories, a roughly 2.48 MB JSON manifest, and a roughly 1.93 MB PNG atlas.
  The ID count is not an expected client download count: most records are not
  item-like objects and EAM will request images only for objects it renders.
- A live extraction completed on both Windows and Linux with identical visual
  and PNG catalog hashes. It rendered all 14,655 objects into 6,201 unique
  content-addressed PNGs totaling 2,566,427 bytes.
- The extraction took approximately 16 seconds on Windows and 18 seconds in
  Linux, but both runs peaked near 3.2 GB of working memory. That memory use is
  the main issue to resolve before choosing production worker limits.
- Full measurements and hashes are recorded in
  [proof-of-concept results](proof-of-concept.md).

These values are observations, not permanent API constants.

## Decisions to lock before the public contract

### Canonical source and version identity

The first service version should use one explicitly configured canonical Realm
platform, provisionally the Windows standalone build that the existing EAM
pipeline has already validated.

An update hint should identify the observed Realm build and platform:

```json
{
  "platform": "windows",
  "observedBuildHash": "b14d91945492e348d572f7ae72f273cd"
}
```

The public game-data build identifier should not be only Realm's build hash or
only the MD5 of `resources.assets`. Rebuilding the same source after an
extractor, renderer, or schema correction must produce a distinct service
build. The recommended identifier is a SHA-256 derived from:

```text
canonical source checksum
+ extractor commit/version
+ renderer version
+ API schema version
```

Realm build hashes remain provenance and update-hint values. The derived
service build ID identifies immutable API output and is used by manifest and
diff URLs.

### Entity scope

The current 14,655 records include several model categories, not only equipment.
Before naming the root collection `items`, inspect and count each extracted
category such as equipment, skins, pet skins, dyes, emotes, and entrances.

The generic contract should either:

- expose a neutral `objects` collection with a `kind` field; or
- expose separate typed collections while providing an EAM compatibility map.

The API should not permanently call every renderable game object an item only
because the first consumer does so.

### Storage and delivery

PostgreSQL `BYTEA` plus per-instance memory caching is the initial implementation
default. Before production deployment, confirm whether Maik's infrastructure
already provides S3-compatible object storage or a static CDN origin.

Individual content-addressed PNGs remain the preferred update format. The full
catalog contains 14,655 possible IDs, but clients are expected to request only
the relevant, visible subset. The proof of concept must measure:

- the number of unique image hashes;
- total and average PNG storage after deduplication;
- extraction and PNG-encoding time;
- per-category object counts; and
- a realistic EAM vault view's cold and warm request counts.

Do not add a full image bundle endpoint unless real client measurements show it
is useful. EAM should request only relevant visible sprites and cache them
locally by content hash. The API may split metadata catalogs by object kind so
consumers do not need unrelated records.

### Database conventions

Confirm whether the production services use EF Core migrations, direct Npgsql,
or another established database approach. Also confirm whether migrations run
from application startup, an init container, or a dedicated deployment job.

The proof of concept can use EF Core with PostgreSQL unless Maik specifies an
existing convention.

## Required implementation spikes

### 1. Linux extraction

Completed for the first live source on 2026-08-19. The Linux output matched
Windows exactly and had no native-library or case-sensitive-path failures.
Peak memory was approximately 3.2 GB, so the extractor must either be optimized
or the updater pod must be provisioned with a safely measured memory limit.

Run the complete updater inside the intended Linux Docker image using the live
canonical `resources.assets.gz` file. Record:

- successful extracted counts by category;
- wall-clock duration;
- peak memory and temporary disk usage;
- final database and sprite size; and
- any native-library or case-sensitive-path failures.

Run the same input twice and verify deterministic normalized records and image
pixels.

### 2. Hash and renderer determinism

Use two hashes for generated images:

- a visual hash over normalized dimensions and RGBA pixels; and
- a blob hash over the exact PNG bytes used by the immutable URL.

When the visual hash is unchanged, a new encoder version should be able to
reuse the previous PNG blob instead of marking every sprite as visually
modified. Intentional renderer changes increment the renderer version included
in the service build ID.

### 3. Data parity

Compare live extracted output against the currently accepted EAM definitions:

- item IDs and required metadata fields;
- player-stat index, ID, display name, and dungeon flags;
- fame-bonus groups, categories, values, repeatability, and conditions; and
- duplicate IDs and deterministic conflict resolution.

Produce a machine-readable parity report. Differences must be classified as a
new live value, an intentional normalization, an extractor gap, or an EAM-only
presentation value.

### 4. Publication safety

Before a build becomes latest, validate minimum counts and compare it with the
previous build. Large unexplained deletion or modification percentages should
fail publication and require inspection rather than distributing a likely
parser regression.

The initial thresholds should be based on the first two real build comparisons,
not guessed permanently in code.

### 5. Multi-instance hint behavior

Start two API instances and one worker against the same PostgreSQL database.
Submit duplicate and random update hints concurrently and verify:

- hints are deduplicated;
- one global cooldown is enforced;
- at most one official check runs;
- at most one extraction runs;
- callers receive immediate `202 Accepted` responses; and
- the old build remains available throughout publication.

## Operational preparation

Before production deployment, define:

- repository owner and container registry;
- production hostname and CDN behavior;
- PostgreSQL secret and migration conventions;
- CPU, memory, and temporary-storage requests based on the Linux spike;
- retained build count and sprite garbage-collection policy;
- metrics for last check, last successful publication, duration, counts, hints,
  failures, and HTTP 429 responses;
- an alert when the updater repeatedly fails or a reported build remains
  unpublished; and
- responsibility for Kubernetes manifests and deployment approval.

Also confirm that the intended distribution of extracted game metadata and
images is acceptable to the service owner, and review licenses for the
extractor's image-processing dependencies before public deployment.

## Questions for the service operator

The remaining infrastructure questions can be sent as one message:

> The design uses two or more stateless ASP.NET API pods, one lightweight
> updater worker, and PostgreSQL for coordination. Do you already have
> S3-compatible object storage or a CDN/static origin for content-addressed
> PNGs, or should version 1 store them as `BYTEA` in PostgreSQL? Do your .NET
> services have an established EF Core/Npgsql migration pattern? Finally, do
> you want this repository to provide Kubernetes manifests, or will deployment
> configuration live in your infrastructure repository?

## Proof-of-concept exit criteria

EAM integration should begin after all of the following are true:

- Linux Docker extraction succeeds from a live canonical build.
- A build survives API and container restarts in PostgreSQL.
- Repeating the same input publishes no duplicate build or sprite content.
- A complete manifest and representative sprites pass visual and data checks.
- Update hints are coalesced safely across two API instances.
- A failed refresh leaves the previous build fully usable.
- The API contract has a derived service build ID and explicit entity scope.
