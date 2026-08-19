# Development and Testing

## Intended local environment

Development will use Docker Compose even when a developer already operates a
PostgreSQL server. The isolated Compose database makes setup reproducible and
prevents test data or migrations from affecting an existing database.

Planned services:

| Service | Purpose |
| --- | --- |
| `postgres` | Isolated PostgreSQL database with a named volume. |
| `api` | ASP.NET application running in `serve` mode on port 8080. |
| `updater` | One-shot invocation of the same image in `refresh` mode. |
| `gateway` | Optional scale-test reverse proxy for multiple API instances. |

The Compose file and application do not exist yet. Once implemented, the
target workflow is:

```powershell
docker compose up --build -d postgres api
docker compose run --rm updater
```

Expected manual inspection points:

```text
http://localhost:8080/swagger
http://localhost:8080/api/v1/builds/latest
```

Swagger is enabled only in development. Production deployments expose the
documented read-only routes without an interactive API UI.

## Prerequisites

- Git with submodule support
- Docker with Docker Compose
- Optional .NET 8 SDK for running tests outside Docker

The service will reference `TadusPro/RotMGAssetExtractor` as a pinned Git
submodule during the proof of concept. A versioned NuGet package can replace
the submodule later if maintaining a package provides enough benefit.

Planned checkout setup:

```powershell
git submodule update --init --recursive
```

## Configuration principles

- Commit an `.env.example`, never a real `.env` containing credentials.
- Use ASP.NET configuration and environment variables.
- Use an isolated local database by default.
- Receive production secrets from Kubernetes Secrets.
- Keep Realm endpoints configurable for tests, while validating trusted hosts
  in production.
- Do not include a public refresh endpoint.

Likely configuration sections include:

```text
ConnectionStrings__GameData
Realm__AppInitUrl
Realm__AllowedCdnHosts__0
Storage__RetainedBuilds
RateLimiting__MetadataPermitLimit
RateLimiting__SpriteConcurrencyLimit
```

Names remain provisional until the corresponding options classes exist.

## Test layers

### Unit tests

Unit tests run without Docker or internet access and cover:

- Parsing saved Realm app-init XML.
- Parsing saved checksum JSON.
- Selecting the exact `resources.assets` path.
- Rejecting untrusted CDN hosts and invalid paths.
- Mapping extractor models into stable API records.
- Canonical metadata hashing.
- Added, modified, removed, and unchanged comparisons.
- Cache and ETag behavior.

Small test fixtures are committed to the repository. The 47 MB game asset file
is not committed.

### PostgreSQL integration tests

Integration tests use an isolated PostgreSQL container and cover:

- Applying database migrations to an empty database.
- Publishing a complete build transactionally.
- Rejecting duplicate build hashes without corrupting state.
- Deduplicating identical sprite hashes.
- Keeping the previous build active after an intentional publication failure.
- Returning the same latest build from separate API application instances.

### Live extraction smoke test

A manual or scheduled test downloads the current Realm
`resources.assets.gz`, runs the real extractor in a Linux container, and checks:

- The upstream MD5 matches after decompression.
- Extraction returns a plausible non-zero item count.
- Every referenced sprite exists and its SHA-256 is correct.
- PNG dimensions and transparency are valid.
- Player stats and fame bonuses are present.
- A representative set of sprites can be inspected visually.

The live test should not run on every pull request because it depends on an
external service and downloads a comparatively large file. Normal CI uses
fixtures; the live workflow can be manually dispatched or scheduled
infrequently.

### End-to-end Compose test

The first end-to-end acceptance sequence is:

1. Start PostgreSQL and one API container.
2. Run the updater and confirm a new build is published.
3. Request `latest`, the full manifest, and several sprites.
4. Run the updater again and confirm it reports `unchanged` without creating a
   duplicate build.
5. Restart the API and confirm the build remains available from PostgreSQL.
6. Configure an invalid upstream endpoint, run the updater, and confirm the
   previous build remains available.
7. Start two API instances behind the optional gateway and confirm both serve
   identical hashes and bytes.
8. Point an EAM development build at the local API and compare representative
   item rendering with the accepted visual baseline.

### Client update test

To validate diffs, retain two real or fixture builds:

1. Cache the older complete manifest and its referenced images.
2. Request the delta to the newer build.
3. Download only new sprite hashes.
4. Apply additions and modifications and remove deleted IDs.
5. Compare the resulting local manifest byte-for-byte or structurally with the
   newer complete manifest.
6. Confirm that a failed sprite download leaves the old manifest active.

## Planned implementation commits

The initial implementation should remain reviewable through focused commits:

1. `docs: define game data service architecture`
2. `chore: scaffold ASP.NET service and Docker build`
3. `feat: discover and download current Realm resources`
4. `feat: extract normalized game data and sprites`
5. `feat: persist immutable builds in PostgreSQL`
6. `feat: serve manifests, diffs, and sprites`
7. `chore: add Docker Compose development environment`
8. `test: cover publication, fallback, and multi-instance behavior`

The EAM integration belongs in a separate EAM branch after the service proof
of concept is running successfully.
