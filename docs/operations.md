# Operations

## Deployment boundary

The Compose deployment is for development and single-host use. It mirrors the
intended multi-instance topology with two stateless API replicas, one updater,
PostgreSQL, a gateway, and a backup process. The gateway defaults to loopback.
Use a private address for LAN access or place it behind a TLS reverse proxy for
public access.

PostgreSQL must not be published. The updater is the only application service
that needs outbound Realm access.

Cluster deployments use the shared EAM PostgreSQL instance and the existing
ingress instead; see [Kubernetes mapping](#kubernetes-mapping).

## Host preparation

Create a deployment root and an untracked `.env` with mode `0600`. The bind
directories must be writable by the container users: UID 70 for PostgreSQL and
backups, and UID 1654 for the .NET worker.

```sh
install -d -m 0750 /srv/rotmg-game-data/{postgres,backups,worker}
chown -R 70:70 /srv/rotmg-game-data/postgres /srv/rotmg-game-data/backups
chown -R 1654:1654 /srv/rotmg-game-data/worker
```

Example environment:

```dotenv
POSTGRES_PASSWORD=<long-random-secret>
DATA_ROOT=/srv/rotmg-game-data
BIND_ADDRESS=127.0.0.1
HTTP_PORT=8090
```

## Rollout

Build with refreshed base images, start the stack, and wait for health:

```sh
docker compose build --pull
docker compose up -d --remove-orphans
docker compose ps
curl --fail http://127.0.0.1:8090/health/ready
```

The PostgreSQL derivative applies available Alpine security updates and runs
directly as UID 70. The NGINX derivative applies updates and runs as its
unprivileged user with a read-only root filesystem.

A first deployment can publish synchronously:

```sh
docker compose run --rm --no-deps worker refresh
```

Normal operation does not require this command: the long-running worker checks
immediately when no build exists, every six hours, and after persisted update
hints.

## Backups

The backup container creates a custom-format dump immediately after startup and
then every 24 hours. It writes a `.partial` file first, atomically renames a
successful dump, and removes completed dumps older than 14 days.

List and validate the newest dump:

```sh
latest=$(find "$DATA_ROOT/backups" -type f -name 'rotmg-game-data-*.dump' -printf '%T@ %f\n' | sort -nr | head -n1 | cut -d' ' -f2-)
docker compose exec -T backup pg_restore --list "/backups/$latest" >/dev/null
```

Also copy backups off the Docker host. Local retention protects against a bad
container or deployment, but not host or disk loss.

To test restoration without touching production, create a separate temporary
PostgreSQL instance/database and restore the dump there. Do not restore over the
live database without an approved outage and a separately verified backup.

## Monitoring

Monitor at minimum:

- `GET /health/ready` for API and database readiness.
- `GET /api/v1/status` for stale checks, pending hints, and refresh errors.
- container restart counts and unhealthy states.
- worker logs for extraction or official metadata failures.
- backup health and the age/size of the newest dump.
- free space under `DATA_ROOT`.

The latest successful build remains available if the worker cannot download,
extract, validate, or publish a newer build.

## Rollback

Application releases are stateless apart from PostgreSQL and the worker cache.
To roll back, start the previous source/image revision with the same protected
`.env` and `DATA_ROOT`. Schema creation is additive and idempotent. Do not
delete a newer build or restore PostgreSQL merely to roll back API code unless
an explicit database incompatibility has been identified.

## Kubernetes mapping

The Compose stack in this repository is for development and single-host use. In
the EAM cluster only the application image is deployed:

- an API Deployment with at least two `serve` replicas;
- one `worker` replica (the advisory lock still prevents overlap);
- the shared PostgreSQL instance used by the other EAM APIs;
- the existing ingress, which provides TLS and cluster-wide rate limiting.

The `postgres`, `gateway` and `backup` services from `compose.yaml` are **not**
deployed. The backup process in particular runs `pg_dump` against the whole
database: against the shared instance it would capture every other service's
tables, so central cluster backups are used instead.

Keep API pods stateless and give the worker ephemeral scratch space at the path
in `Realm__WorkDirectory`. The runtime image is chiseled and already runs as UID
1654, so mounted volumes must be writable by that UID.

### Database configuration

In the cluster the service reads the same variables as the other EAM APIs,
injected from Kubernetes Secrets:

| Variable | Purpose |
| --- | --- |
| `DATABASE_HOST` | PostgreSQL host. |
| `DATABASE_PORT` | PostgreSQL port; defaults to 5432 when unset. |
| `DATABASE_NAME` | Database name. |
| `DATABASE_USERNAME` | Role name. |
| `DATABASE_PASSWORD` | Role password. |
| `DATABASE_LOGGING` | `true` raises Npgsql logging to Information. |
| `DEBUG_MODE` | When set, disables TLS to the database for local use. |
| `PORT` | Listen port. Takes precedence over `ASPNETCORE_HTTP_PORTS`. |

Transport encryption to the database is required unless `DEBUG_MODE` is set, and
the server certificate is not verified, matching `eam-api-commons`. Setting
`ConnectionStrings__GameData` overrides all of the above and is how the Compose
stack configures its own PostgreSQL container.

Each replica keeps a deliberately small connection pool because the instance is
shared with every other EAM API.

### Shared-database safety

All of this service's tables are prefixed `game_data_`:

```text
game_data_builds        game_data_sprites       game_data_build_sprites
game_data_build_diffs   game_data_update_hints  game_data_refresh_state
```

Schema creation only ever adds objects, never drops or alters them, so it cannot
affect tables owned by another service. The statements run under a
transaction-scoped advisory lock so simultaneous pod starts cannot collide on
concurrent DDL. The names are also registered in `eam-api-commons` as reserved,
which makes a colliding Sequelize model fail fast during development.
