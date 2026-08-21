# Architecture

## Context

Realm client updates can add, remove, or modify a small number of assets while
leaving most game data unchanged. Shipping an extractor with every desktop
client duplicates work, increases application size, and introduces
platform-specific packaging concerns. This service performs extraction once
and publishes reusable results for all consumers.

The target production ASP.NET API runs behind a load balancer with at least two
instances. The update workload is infrequent and runs in a separate worker.

## High-level design

```text
Realm build metadata --------> updater worker ---> RotMGAssetExtractor
                                     ^                    |
                                     |                    |
                               update hints        generated data
                                     |                    |
                                     +------ PostgreSQL <-+
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

- `serve`: starts the ASP.NET API and accepts read requests and update hints.
- `worker`: waits for update hints, performs scheduled checks, and publishes
  confirmed builds.
- `refresh`: performs the worker's check once and exits for development and
  recovery tasks.

## Public update hints

Clients often observe a new game build before the scheduled service check. An
unauthenticated client may submit its observed build hash to:

```http
POST /api/v1/update-hints
```

The hint is untrusted and never directly starts a download from a client-chosen
URL. The API validates the hash shape, records or coalesces it in PostgreSQL,
signals the updater worker, and immediately returns `202 Accepted`. The caller
does not wait for the check or extraction.

No queue service is required. The persisted row is authoritative and the worker
polls pending state every ten seconds. Restarts therefore cannot lose work.

When a reported hash differs from the latest published build, the worker makes
one inexpensive request to Realm's official build-metadata endpoint. Only a
different hash returned by that official endpoint can authorize downloading
`resources.assets.gz` and running the extractor.

Abuse is bounded in three ways:

- Repeated reports of the same hash update one database record.
- A cluster-wide cooldown permits at most one official metadata check during a
  configured interval, initially five minutes.
- A per-client rate limit rejects excessive hint submissions before they reach
  the database.

Random hashes can therefore produce at most one small official metadata request
per cooldown period, not repeated downloads or extractions. Hints remain
pending in PostgreSQL if the worker is temporarily unavailable.

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

For each renderable object, the updater creates:

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
build by stable object ID:

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

PostgreSQL is the source of truth. The implemented logical model is:

```text
game_data_builds
  build_id, realm_build_hash, source_checksum, schema_version,
  generated_at, manifest_json, manifest_hash, is_latest

game_data_sprites
  sprite_hash, png_bytes, width, height, created_at

game_data_build_sprites
  build_id, sprite_hash

game_data_build_diffs
  from_build_id, to_build_id, diff_json, diff_hash

game_data_update_hints
  observed_build_hash, first_seen_at, last_seen_at, report_count

game_data_refresh_state
  last_checked_at, last_successful_at, last_official_check_at,
  pending_hint_at, last_error_at, last_error
```

Every table carries the `game_data_` prefix because the deployed instance shares
one PostgreSQL database with the other EAM APIs. The names are registered as
reserved in `eam-api-commons` so a colliding Sequelize model in another service
fails at startup rather than competing for the table.

The schema is created idempotently at process startup. Manifest and diff JSON
are stored as deterministic UTF-8 bytes. Sprite rows are content-addressed and
deduplicated, with explicit references from retained builds.

Storing the initial output in PostgreSQL keeps deployment requirements small.
If traffic or storage measurements justify it, sprite bytes can later move to
S3-compatible object storage without changing public content-addressed URLs.

## Atomic publication and failure handling

A new build is generated and validated before it becomes visible:

1. Download and extract in temporary storage.
2. Validate that required sections exist and item counts are plausible.
3. Validate every generated image and calculate hashes.
4. Insert missing content-addressed sprites.
5. Insert missing sprites, the build manifest, sprite references, and diff in
   one database transaction.
6. Commit the transaction, making the build available to API instances.

A failed update never changes the latest successful build. Temporary files are
removed and the process exits with a non-zero status so Docker or Kubernetes
can report the failure.

PostgreSQL unique constraints make build publication idempotent. The updater
uses a PostgreSQL advisory lock so a manual one-shot refresh cannot overlap the
long-running worker or another accidentally started worker instance.

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
- Update hints are limited per client and share a PostgreSQL-backed global
  check cooldown.
- Sprite endpoints use generous burst and concurrency limits because one UI
  view may legitimately request many images.
- Health endpoints are excluded.

Forwarded client addresses are trusted only from configured load balancer
proxies. Public hints may wake the updater, but cannot choose an upstream URL or
authorize extraction. The updater also checks metadata on a schedule, retries
transient failures a small number of times with backoff and jitter, and
downloads only after the official endpoint reports a new build.

## Deployment model

Development and single-host deployment use Docker Compose with PostgreSQL, two
API instances, one updater worker, an NGINX gateway, and daily retained
backups. The same worker mode supports a one-shot invocation for manual tests.

Production uses the same image in two Kubernetes workloads:

- A Deployment with at least two `serve` replicas.
- A Deployment with one lightweight `worker` replica.

The worker wakes for persisted client hints and also checks every few hours as a
fallback. Extraction still occurs only when Realm's official endpoint confirms
a new build.
