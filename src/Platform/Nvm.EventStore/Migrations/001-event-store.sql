IF SCHEMA_ID('es') IS NULL EXEC('CREATE SCHEMA es');

IF OBJECT_ID('es.Streams', 'U') IS NULL
CREATE TABLE es.Streams (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    StreamId varchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    StreamType varchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Version bigint NOT NULL CONSTRAINT DF_EsStreams_Version DEFAULT 0,
    CreatedAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_EsStreams PRIMARY KEY (SiteId, StreamId),
    CONSTRAINT CK_EsStreams_Version CHECK (Version >= 0)
);

IF OBJECT_ID('es.Events', 'U') IS NULL
CREATE TABLE es.Events (
    GlobalSequence bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_EsEvents PRIMARY KEY,
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    StreamId varchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Version bigint NOT NULL,
    SourceEventId uniqueidentifier NOT NULL,
    EventType varchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SchemaVersion int NOT NULL,
    PayloadJson nvarchar(max) NOT NULL,
    MetadataJson nvarchar(max) NOT NULL,
    CloudEventJson nvarchar(max) NOT NULL,
    OccurredAt datetimeoffset(7) NOT NULL,
    RecordedAt datetimeoffset(7) NOT NULL,
    CONSTRAINT FK_EsEvents_Streams FOREIGN KEY (SiteId, StreamId) REFERENCES es.Streams(SiteId, StreamId),
    CONSTRAINT UQ_EsEvents_StreamVersion UNIQUE (SiteId, StreamId, Version),
    CONSTRAINT UQ_EsEvents_SourceEventId UNIQUE (SourceEventId),
    CONSTRAINT CK_EsEvents_Version CHECK (Version > 0),
    CONSTRAINT CK_EsEvents_SchemaVersion CHECK (SchemaVersion > 0),
    CONSTRAINT CK_EsEvents_PayloadJson CHECK (ISJSON(PayloadJson) = 1),
    CONSTRAINT CK_EsEvents_MetadataJson CHECK (ISJSON(MetadataJson) = 1),
    CONSTRAINT CK_EsEvents_CloudEventJson CHECK (ISJSON(CloudEventJson) = 1)
);

-- The M5 prototype schema may have been created locally before canonical envelopes
-- were persisted. No production migration exists yet; an occupied legacy table needs
-- an explicit, reviewed backfill so that immutable facts are never silently rewritten.
IF COL_LENGTH('es.Events', 'CloudEventJson') IS NULL
BEGIN
    IF EXISTS (SELECT 1 FROM es.Events)
        THROW 51001, 'Legacy event rows require an explicit CloudEventJson backfill.', 1;
    ALTER TABLE es.Events ADD CloudEventJson nvarchar(max) NULL;
    ALTER TABLE es.Events ALTER COLUMN CloudEventJson nvarchar(max) NOT NULL;
END;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE parent_object_id = OBJECT_ID('es.Events') AND name = 'CK_EsEvents_CloudEventJson')
    ALTER TABLE es.Events ADD CONSTRAINT CK_EsEvents_CloudEventJson CHECK (ISJSON(CloudEventJson) = 1);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('es.Events') AND name = 'IX_EsEvents_SiteGlobalSequence')
CREATE INDEX IX_EsEvents_SiteGlobalSequence ON es.Events(SiteId, GlobalSequence);

IF OBJECT_ID('es.Snapshots', 'U') IS NULL
CREATE TABLE es.Snapshots (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    StreamId varchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Version bigint NOT NULL,
    StateJson nvarchar(max) NOT NULL,
    RecordedAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_EsSnapshots PRIMARY KEY (SiteId, StreamId, Version),
    CONSTRAINT FK_EsSnapshots_Streams FOREIGN KEY (SiteId, StreamId) REFERENCES es.Streams(SiteId, StreamId),
    CONSTRAINT CK_EsSnapshots_Version CHECK (Version > 0 AND Version % 100 = 0),
    CONSTRAINT CK_EsSnapshots_StateJson CHECK (ISJSON(StateJson) = 1)
);

IF OBJECT_ID('es.Outbox', 'U') IS NULL
CREATE TABLE es.Outbox (
    EventId uniqueidentifier NOT NULL CONSTRAINT PK_EsOutbox PRIMARY KEY,
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    StreamId varchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Version bigint NOT NULL,
    EventType varchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SchemaVersion int NOT NULL,
    PayloadJson nvarchar(max) NOT NULL,
    MetadataJson nvarchar(max) NOT NULL,
    OccurredAt datetimeoffset(7) NOT NULL,
    RecordedAt datetimeoffset(7) NOT NULL,
    CreatedAt datetimeoffset(7) NOT NULL,
    DispatchedAt datetimeoffset(7) NULL,
    Attempt int NOT NULL CONSTRAINT DF_EsOutbox_Attempt DEFAULT 0,
    NextAttemptAt datetimeoffset(7) NOT NULL,
    ClaimId uniqueidentifier NULL,
    LastError nvarchar(2000) NULL,
    CONSTRAINT FK_EsOutbox_Events FOREIGN KEY (EventId) REFERENCES es.Events(SourceEventId),
    CONSTRAINT CK_EsOutbox_Attempt CHECK (Attempt >= 0),
    CONSTRAINT CK_EsOutbox_PayloadJson CHECK (ISJSON(PayloadJson) = 1),
    CONSTRAINT CK_EsOutbox_MetadataJson CHECK (ISJSON(MetadataJson) = 1)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('es.Outbox') AND name = 'IX_EsOutbox_Pending')
CREATE INDEX IX_EsOutbox_Pending ON es.Outbox(NextAttemptAt, CreatedAt)
    INCLUDE (SiteId) WHERE DispatchedAt IS NULL;

-- Earlier M5 installations can already contain committed events. Upgrade is an
-- explicit deployment operation; seed their missing publish intents idempotently.
INSERT INTO es.Outbox
    (EventId, SiteId, StreamId, Version, EventType, SchemaVersion,
     PayloadJson, MetadataJson, OccurredAt, RecordedAt, CreatedAt, NextAttemptAt)
SELECT e.SourceEventId, e.SiteId, e.StreamId, e.Version, e.EventType, e.SchemaVersion,
       e.PayloadJson, e.MetadataJson, e.OccurredAt, e.RecordedAt, e.RecordedAt, e.RecordedAt
FROM es.Events AS e
WHERE NOT EXISTS (SELECT 1 FROM es.Outbox AS o WHERE o.EventId = e.SourceEventId);

IF DATABASE_PRINCIPAL_ID('nvm_app') IS NOT NULL
BEGIN
    GRANT SELECT, INSERT ON es.Streams TO nvm_app;
    GRANT UPDATE (Version) ON es.Streams TO nvm_app;
    DENY DELETE ON es.Streams TO nvm_app;
    GRANT SELECT, INSERT ON es.Events TO nvm_app;
    DENY UPDATE, DELETE ON es.Events TO nvm_app;
    GRANT SELECT, INSERT ON es.Snapshots TO nvm_app;
    DENY UPDATE, DELETE ON es.Snapshots TO nvm_app;
    GRANT SELECT, INSERT ON es.Outbox TO nvm_app;
    GRANT UPDATE (Attempt, NextAttemptAt, ClaimId, LastError, DispatchedAt) ON es.Outbox TO nvm_app;
    DENY DELETE ON es.Outbox TO nvm_app;
END;
