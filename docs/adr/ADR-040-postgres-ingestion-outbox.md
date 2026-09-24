# ADR-040 — PostgreSQL outbox for ingestion measurement events

| Field | Value |
|---|---|
| Status | Accepted, 2026-09-23 |
| Scope | M6 ingestion → bus dual-write; ADR-022 partial parallel-writer commit |

## Context

Ingestion stores telemetry in PostgreSQL, while M6's event-store outbox is in SQL Server. The SQL Server transaction cannot include the PostgreSQL row. Each parallel ingestion writer also commits independently: if another writer fails, `Task.WhenAll` throws before the old in-process publish call, and replay deduplicates the already committed row. Its event is then absent indefinitely.

## Decision

When bus publishing is enabled, each `WriteChunkAsync` transaction inserts one `ingest.measurement_outbox` row for each newly claimed, eligible `MeasurementRecorded`. The outbox primary key is the same `source_event_id` used for ingestion deduplication and the event's `EventId`. A duplicate ingestion cannot create another intent. The intent carries the event payload captured at the original commit; later signal configuration changes do not change its meaning.

An independent hosted dispatcher selects due rows using `FOR UPDATE SKIP LOCKED`, publishes through the existing MassTransit publisher, then marks a successful row as published. Failed sends remain pending with capped exponential retry. Multiple dispatcher instances can work without selecting the same pending row at once. The dispatcher owns its own database transaction; it never waits for a gateway replay to recover.

The default ingestor constructor keeps the M2 direct-publish behavior for existing callers. The ingestion host enables the transactional path whenever bus publishing is configured and starts the dispatcher. A deployment must apply migration 015 before starting the new host.

## Delivery contract

There is exactly one **durable intent and logical EventId** per eligible committed telemetry row. Broker delivery is **at least once**. A process or PostgreSQL failure after broker acceptance but before the `published_at` update commits can replay the same EventId. No transaction spans PostgreSQL and RabbitMQ, so a claim of exactly one physical delivery would be false. Every downstream handler must deduplicate the stable EventId before applying its business effect (K7). The integration test forces precisely this receipt failure and verifies replay uses the same ID.

Existing telemetry committed before migration 015 has no intent and is not automatically backfilled. Its original signal whitelist and publication decision cannot be reconstructed reliably from today's configuration. This outbox closes the gap for newly committed rows after activation.
