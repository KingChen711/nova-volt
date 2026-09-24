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
It does not yet replace the fixture-backed POM/WIP pages, project quality/location transitions,
or implement genealogy/trace queries. These remain open in the project completion tracker.
Existing credential rotation scripts cover the original infrastructure accounts; they do not
rotate these two new role passwords. Rotate them explicitly in the databases before updating
`.env` and recreating this worker; rerunning migration is not password rotation.
