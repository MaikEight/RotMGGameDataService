# Development and Testing

## Prerequisites

- Git with submodule support
- Docker with Docker Compose
- Optional .NET 8 SDK for host-side builds and tests

Initialize the pinned extractor dependency after cloning:

```powershell
git submodule update --init --recursive
```

## Offline build and tests

```powershell
dotnet restore RotMGGameDataService.sln
dotnet build RotMGGameDataService.sln --configuration Release --no-restore
dotnet test RotMGGameDataService.sln --configuration Release --no-build
dotnet list src/RotMGGameDataService/RotMGGameDataService.csproj package --vulnerable --include-transitive
```

The tests do not require the Realm servers. They cover trusted URL selection and
rejection, deterministic JSON and byte hashing, the object catalog hash, build
identity, and diff construction. Rendering and model mapping are not covered,
because both need a real `resources.assets`; a live refresh is intentionally
separate for the same reason.

## Compose environment

Create an untracked `.env` from `.env.example`. For local development, use a
repository-local data directory:

```dotenv
POSTGRES_PASSWORD=replace-with-a-long-random-hex-value
DATA_ROOT=./.data
BIND_ADDRESS=127.0.0.1
HTTP_PORT=8090
```

Start all services:

```powershell
docker compose up --build --detach
docker compose ps
```

The stack contains:

| Service | Purpose |
| --- | --- |
| `postgres` | Authoritative build, sprite, diff, and updater state. |
| `api-a`, `api-b` | Interchangeable read API replicas. |
| `worker` | Scheduled checks and persisted update-hint processing. |
| `gateway` | Unprivileged NGINX load balancer and only published port. |
| `backup` | Immediate and daily custom-format PostgreSQL dump with 14-day retention. |

The database and internal API ports are not published. Only the configured
gateway address and port are reachable from the host.

## Live refresh

The worker checks on startup, but a one-shot command is useful for deterministic
development output:

```powershell
docker compose run --rm --no-deps worker refresh
```

On the first run this downloads and verifies the current
`resources.assets.gz`, extracts all supported data, renders sprites, validates
the result, and publishes it in one transaction. A later unchanged run performs
only the small official metadata/checksum checks.

Useful inspection commands:

```powershell
Invoke-RestMethod http://127.0.0.1:8090/api/v1/builds/latest
Invoke-RestMethod http://127.0.0.1:8090/api/v1/status
docker compose logs --tail 100 worker
```

## Application modes

The same application image supports:

- `serve`: run the ASP.NET API.
- `worker`: poll persisted hints and perform a scheduled check every six hours.
- `refresh`: perform one advisory-lock-protected check and exit.
- `healthcheck <url>`: exit zero only for a successful HTTP response.

## Acceptance checks

Before merging deployment changes:

1. Run the offline build, tests, and NuGet vulnerability audit.
2. Validate the Compose model with non-secret test environment values.
3. Build all images with fresh bases using `docker compose build --pull`.
4. Start the stack and wait for every health check.
5. Publish or confirm the current live build.
6. Verify manifest and sprite SHA-256 values against downloaded bytes.
7. Verify ETag `304`, immutable cache headers, and gzip/Brotli compression.
8. Exercise malformed JSON, unknown fields, oversized bodies, bad IDs, missing
   records, unsupported media types, and rate limits.
9. Stop one API replica and confirm the gateway continues serving requests.
10. Run a concurrent metadata and sprite read test and check logs for 5xx
    responses or PostgreSQL pool exhaustion.
11. Validate the newest backup with `pg_restore --list`.
12. Scan the final runtime images for known vulnerabilities.

## Configuration and trust

Configuration uses ASP.NET environment-variable conventions. The committed
defaults constrain downloads to Realm's configured HTTPS app-init endpoint,
allow-listed CDN hosts, one exact resource path, and fixed compressed and
uncompressed size ceilings.

Never commit `.env`. A public update hint contains only a 32-character observed
Realm build hash; unknown JSON properties are rejected so clients cannot add an
upstream URL. Production TLS and cluster-wide rate limiting belong at the
ingress or CDN.

## Extractor updates

`vendor/RotMGAssetExtractor` is a pinned Git submodule. Updating it changes the
service build identity and therefore produces a new immutable build even if
Realm's source checksum is unchanged. Update the submodule deliberately, run
the full acceptance checks, and preserve renderer compatibility tests.

See [operations.md](operations.md) for rollout, backup, restore, and rollback
procedures.
