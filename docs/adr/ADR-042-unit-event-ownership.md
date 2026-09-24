# ADR-042 — Unit lifecycle facts belong to Traceability

Status: Accepted, 2026-09-24. Architecture clarification within the delegated delivery scope.

## Context

Scope M5 places the event-sourced ProductionUnit aggregate and SerializeUnit, StartStep,
CompleteStep and RecordMeasurement commands in Traceability. The initial event catalog placed
ProcessStepStarted/Completed in ProductionExecution. Keeping those competing ownership rules
would split one stream's transition validation or encourage direct FB references.

## Decision

Traceability owns the current ProductionUnit lifecycle and its five facts:
ProductionUnitSerialized, ProcessStepStarted, ProcessStepCompleted, UnitMeasurementRecorded,
and DuplicateSerialDetected. Their existing v1 event names use the `traceability` context.
ProductionExecution retains DataCollectionRecorded: recording an operator's measurement alone
does not complete a process step or make a quality decision (ADR-038).

HTTP paths `/commands/production/start-step` and `/commands/production/complete-step` remain
operator-facing actions hosted by Execution; URL grouping does not change event ownership.
Other FBs consume Contracts through the bus. No FB references another FB's implementation.

Unit events identify the serial in CloudEvents subject, use site/serial as partition key,
and retain the command/event UUID as correlation and causation for these single-command flows.
Those attributes are stored with each new event and preserved by the outbox publisher.
Previously stored events without optional attributes remain immutable and readable.

## Consequences

Update the catalog/scope ownership cells and lock all five v1 shapes with golden files before
broader consumers are added. A future split of lifecycle responsibilities requires a new decision
and compatible event evolution; it must not rename stored v1 types. This does not close M5/M6:
facets, genealogy, performance labs and the remaining UI are separate acceptance requirements.
