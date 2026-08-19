# Architecture

## Context

Realm client updates can add, remove, or modify a small number of assets while
leaving most game data unchanged. Shipping an extractor with every desktop
client duplicates work, increases application size, and introduces
platform-specific packaging concerns. This service performs extraction once
and publishes reusable results for all consumers.

The production ASP.NET API will run in Kubernetes behind a load balancer with
at least two instances. The update workload is infrequent and does not need to
run in every API instance.

## High-level design

```text
Realm build metadata
        |
        v
One-shot updater ---> RotMGAssetExtractor
        |                    |
        +---- generated game data and PNG sprites
                             |
                             v
                         PostgreSQL
                             |
                    +--------+--------+
                    |                 |
                 API pod A         API pod B
                    +--------+--------+
                             |
                      load balancer/CDN
                             |
                          consumers
```

The application has two modes:

- `serve`: starts the read-only ASP.NET API.
- `refresh`: checks for a new Realm build, publishes it if required, and exits.

The exact command-line syntax will be finalized during implementation.

## Source acquisition

The updater performs the following work:

1. Requests the current standalone Realm build metadata.
2. Downloads the build's `checksum.json` file.
3. Selects `RotMG Exalt_Data/resources.assets` by its exact expected path.
4. Compares the upstream build and file checksum with the latest published
   build.
5. If unchanged, records a successful check and exits without downloading the
   large file.
6. If changed, downloads the corresponding `.gz` object, streams it to
   temporary storage, decompresses it, and verifies the advertised MD5.

The extractor currently needs only `resources.assets`; the service does not
need to install or retain the full Realm client.

Upstream URLs are constructed only from trusted Realm metadata and an allowed
HTTPS CDN host. Public API input must never select an arbitrary download URL or
filesystem path.

## Extraction and normalization

`RotMGAssetExtractor` remains a separate, generic C# library and is pinned to an
exact Git commit. Service-specific mapping lives in this repository.

For each item, the updater creates:

- A normalized metadata record.
- A final transparent PNG tile with the agreed pixel-art scaling, centering,
  and outline.
- A SHA-256 hash of the final PNG bytes.
- A SHA-256 hash of the canonical metadata representation.

The PNG URL is based on its content hash, not the item ID or build ID. An
unchanged image therefore keeps the same URL and remains cached across builds.
Different item IDs may reference the same PNG hash.

The first implementation will not generate a shared sprite atlas. Individual,
content-addressed images make additions, deletions, modifications, caching,
and diffs deterministic.

Player-stat definitions and fame bonuses are normalized from the extractor's
existing `PlayerStat` and `FameBonus` models. Presentation-only concerns remain
with the consuming application.

## Build comparison

The updater compares the new normalized records with the previous successful
build by stable item ID:

- **Added:** the ID did not exist previously.
- **Modified:** its metadata hash or sprite hash changed.
- **Removed:** the ID no longer exists.
- **Unchanged:** both hashes are identical.

The same approach is used for player-stat and fame-bonus sections. A stored
diff allows existing clients to update without downloading an entire manifest
or unchanged images.

The server still downloads the complete upstream `resources.assets.gz` after
an upstream checksum change because Realm exposes checksums per file, not per
individual extracted record.

## Persistence

Kubernetes container filesystems are ephemeral. Temporary downloads and
generated files may use the container's temporary directory, but no published
state relies on it.

PostgreSQL is the initial source of truth. The planned logical model is:

```text
game_data_builds
  build_hash, source_checksum, schema_version, generated_at,
  manifest_bytes, manifest_hash

build_items
  build_hash, item_id, metadata, metadata_hash, sprite_hash

build_player_stats
  build_hash, stat_index, metadata, metadata_hash

build_fame_bonuses
  build_hash, bonus_id, metadata, metadata_hash

sprites
  sprite_hash, png_bytes, width, height, created_at

build_diffs
  from_build_hash, to_build_hash, diff_bytes, diff_hash
```

Exact SQL types, indexes, and migration tooling will be selected during the
database implementation. Sprite rows are content-addressed and deduplicated.
A sprite may be removed only when no retained build references it.

Storing the initial output in PostgreSQL keeps deployment requirements small.
If traffic or storage measurements justify it, sprite bytes can later move to
S3-compatible object storage without changing public content-addressed URLs.

## Atomic publication and failure handling

A new build is generated and validated before it becomes visible:

1. Download and extract in temporary storage.
2. Validate that required sections exist and item counts are plausible.
3. Validate every generated image and calculate hashes.
4. Insert missing content-addressed sprites.
5. Insert the build, normalized records, and diff in one database transaction.
6. Commit the transaction, making the build available to API instances.

A failed update never changes the latest successful build. Temporary files are
removed and the process exits with a non-zero status so Docker or Kubernetes
can report the failure.

PostgreSQL unique constraints make build publication idempotent. Kubernetes
will additionally configure the updater CronJob with
`concurrencyPolicy: Forbid`. A PostgreSQL advisory lock may be added as a
defense-in-depth measure for manual or accidental concurrent refreshes.

## API scaling and caching

API instances are read-only and interchangeable. Each instance may cache
immutable records and sprite bytes in memory; PostgreSQL remains authoritative.
No shared filesystem or Redis dependency is required.

Versioned and content-addressed responses use long-lived immutable caching:

```http
Cache-Control: public, max-age=31536000, immutable
ETag: "<sha256>"
```

The small `latest` document uses a short cache lifetime and supports
conditional requests. A CDN is strongly recommended because public image
traffic is likely to be much larger than update traffic.

## Rate limiting

Rate limiting is applied in layers:

- The CDN or Kubernetes ingress provides the primary cluster-wide policy.
- ASP.NET provides a generous per-instance safety limit.
- Metadata endpoints are limited per client IP.
- Sprite endpoints use generous burst and concurrency limits because one UI
  view may legitimately request many images.
- Health endpoints are excluded.

Forwarded client addresses are trusted only from configured load balancer
proxies. Public requests cannot launch the updater. The updater checks metadata
on a schedule, retries transient failures a small number of times with backoff
and jitter, and downloads only after detecting a new checksum.

## Deployment model

Development uses Docker Compose with PostgreSQL, one API instance, and a
manually invoked one-shot updater. A scale-test profile can add a local reverse
proxy and multiple API instances.

Production uses the same image in two Kubernetes workloads:

- A Deployment with at least two `serve` replicas.
- A CronJob that invokes `refresh` every few hours and exits.

Checking every few hours is inexpensive. Extraction still occurs only when a
new Realm build is detected, regardless of the schedule frequency.
