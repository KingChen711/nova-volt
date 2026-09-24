# Unit projection worker

`Nvm.ProjectionWorker` consumes committed unit events from RabbitMQ, resolves their original
SQL stream/version and commits them to the PostgreSQL inbox before broker acknowledgement.
A background worker applies each ready stream version and marks its inbox row applied in the
same transaction. Duplicate delivery is checked against the original fact; a version gap waits
for the missing event. SQL identity order is not treated as transaction commit order.

## Local deployment

Set private `NVM_PROJECTION_PG_PASSWORD` and `NVM_PROJECTION_SQL_PASSWORD` in ignored `.env`.
Both must be 16–128 characters; the SQL password must meet SQL Server policy.
The existing Execution event-store migration must have run first.

```sh
docker compose --profile projection build projection
docker compose --profile projection run --rm --no-deps projection-migrate
docker compose --profile projection up -d --wait --no-deps projection
```

The explicit migration job creates missing `nvm_projection` logins without changing existing
passwords. PostgreSQL grants only read/insert/update on unit state/checkpoint and read/insert plus
`UPDATE(applied)` on inbox. SQL grants only SELECT on `es.Events`; writes are denied. Runtime
does not receive either administrator credential. `/health/ready` checks both schemas/permissions
and the bus; `/health/live` remains independent of dependencies.

For a first deployment after events were already published, run one reconciliation pass after
starting the worker, so new events remain subscribed while history is read:

```sh
docker compose --profile projection run --rm --no-deps projection --reconcile
```

Reconciliation rescans committed site history and applies stream versions idempotently. It is an
explicit recovery operation, not the continuous worker's polling algorithm. Configure serviced
sites through `NVM_PROJECTIONS__Sites` (demo: NV1,DE1). The current library also supports an
administrative rebuild; runtime intentionally has no DELETE grant, so a destructive reset is
not exposed through this worker.

This slice projects serialization, start/completion and accepted measurements to `rm.unit_current`.
Duplicate-serial incidents project a separate quality hold, including incidents delivered before
the original serialization. New units retain their domain defaults Pending/AtStation; later quality
release, movement and genealogy features remain open.

After the Execution POM migration and projection migration, switch POM query views explicitly:

```sh
docker compose --profile execution run --rm --no-deps execution-prepare-poc --migrate
docker compose --profile projection run --rm --no-deps projection-migrate --connect-pom
```

The API then returns only event-derived units and WIP groups, not an overlay of demonstration rows.
Fixture tables remain untouched. Reconciliation loads historical events before cutover. Each
projection transaction increments a site read revision, so a late low-sequence incident also changes
ETags. POM string limits for step/run/work-order/resource now cover command limits;
refresh consumed OData metadata in Mendix before exercising longer values. Keep WIP key `Id`
at its original maximum length 48: Mendix rejects an in-place length change of an existing
external key during runtime synchronization, even when model checks pass. Its bounded components
(site 3, line 2, step 20, quality 16, three separators) require at most 44 characters.

Manual data collection reads authoritative SQL unit state through a Contracts query hosted by
Traceability in the same command transaction (ADR-043). It does not authorize writes from these
PostgreSQL views. Legacy fixture context is used only if that site/serial has no real unit;
this preserves existing unsent PoC drafts. The explicit Development command fixture preparation
also adds an EOL-only `NV-PACK-DEMO/r1` route at NV1 and DE1, without replacing existing routes.

Existing credential rotation scripts cover the original infrastructure accounts; they do not
rotate these two new role passwords. Rotate them explicitly in the databases before updating
`.env` and recreating this worker; rerunning migration is not password rotation.
