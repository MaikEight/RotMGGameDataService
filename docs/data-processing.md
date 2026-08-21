# Record of Processing Activities entry

This is the Article 30(1) GDPR entry for this service, written to be folded into
the controller's overall record rather than to stand alone. It describes only
the game data service; the other EAM services have their own entries.

Review it whenever the API surface, the logging configuration, or the hosting
arrangement changes.

## Controller

| | |
| --- | --- |
| Name | Maik Kühne IT-Dienstleistungen |
| Address | Maik Kühne, Postfach 1103, 37171 Nörten-Hardenberg, Germany |
| Contact | privacy@maik8.de |

## Processing activity

**Provision of Realm of the Mad God game data to Exalt Account Manager clients.**

### Purpose

Delivering item definitions, item images, player-stat definitions and fame-bonus
definitions so that Exalt Account Manager can display a user's in-game items.
The application cannot render items without this data, so the requests are part
of the software functioning rather than an optional feature.

### Legal basis

Article 6(1)(f) GDPR, legitimate interests. The interest is operating the
application the user chose to install. The processing is limited to what
delivering a response requires and produces no stored record of the user.

### Categories of data subjects

Users of Exalt Account Manager.

### Categories of personal data

| Data | Where it occurs | Retention |
| --- | --- | --- |
| IP address | Held in memory while a request is served, and as a rate-limiting key | Not written to disk; discarded with the rate-limit window |
| IP address in error output | Only when the gateway logs a fault, such as an upstream failure | Rolling container output, capped at 3 x 10 MB per service |

No account identifiers, credentials, payment data, or game-account contents are
transmitted or stored. Requests carry a build identifier and item image content
hashes, neither of which identifies a person.

Request logging is disabled deliberately. An item image is addressed by its
content hash and a client requests only the images it displays, so a request log
paired with a client address would accumulate a record of which items a user
holds. Nothing about operating the service requires that.

### Categories of recipients

None. The data returned is public game data. No processor receives personal data
in the course of this activity beyond the infrastructure provider hosting the
service.

Infrastructure is operated on self-hosted servers in Germany, consistent with
the published privacy policy.

### Transfers to third countries

None.

### Erasure deadlines

No personal data is stored, so no erasure schedule applies to the database. The
service's tables hold game data only: builds, item metadata, item images,
diffs, and updater state. Container output rotates automatically as described
above.

### Technical and organisational measures

- Transport encryption for all client traffic; the client refuses a non-HTTPS
  endpoint outside development builds.
- No request logging. Client addresses are used in memory for rate limiting and
  never persisted.
- Rate limits per client address on metadata, image, bundle and update-hint
  routes.
- The only route accepting input takes a 32-character build hash, which cannot
  select an upstream URL, a filesystem path, or a download.
- Outbound requests are constrained to a configured HTTPS endpoint and
  allow-listed CDN hosts.
- Containers run unprivileged with a read-only root filesystem and
  `no-new-privileges`; the database is not published beyond the internal
  network.
- Responses are immutable and content-addressed, so a consumer can verify what
  it received independently of transport.

## Open items

Recorded here so they are not lost between reviews.

- **Production request logging is not controlled by this repository.** The
  cluster fronts the service with the shared EAM ingress rather than the gateway
  in `compose.yaml`, so the `access_log off` setting here applies only to the
  Compose stack. The ingress needs the equivalent configuration, or a request
  log continues to exist one layer up.
- **The database role is shared with the other EAM services.** This service's
  tables contain no personal data, but the injected credentials can read tables
  belonging to services that do. A role scoped to this service's own tables, or
  a separate database, would remove that. Deferred because the cluster injects
  one set of credentials today.
- **The public update-hint route is unused by Exalt Account Manager.** Disabling
  it at the ingress would reduce the reachable surface without affecting the
  client.
