IF SCHEMA_ID('traceability') IS NULL EXEC('CREATE SCHEMA traceability');

IF OBJECT_ID('traceability.Routes') IS NULL
CREATE TABLE traceability.Routes (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ProductCode nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    RoutingVersion nvarchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
    StepsJson nvarchar(max) NOT NULL,
    TransitionsJson nvarchar(max) NOT NULL,
    CONSTRAINT PK_TraceabilityRoutes PRIMARY KEY (SiteId, ProductCode, RoutingVersion),
    CONSTRAINT CK_TraceabilityRoutes_Steps CHECK (ISJSON(StepsJson) = 1),
    CONSTRAINT CK_TraceabilityRoutes_Transitions CHECK (ISJSON(TransitionsJson) = 1)
);

IF OBJECT_ID('traceability.SerialReservations') IS NULL
CREATE TABLE traceability.SerialReservations (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SerialNumber varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SerializedEventId uniqueidentifier NOT NULL,
    QualityState varchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL
        CONSTRAINT DF_Traceability_Quality DEFAULT ('Pending'),
    LocationState varchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL
        CONSTRAINT DF_Traceability_Location DEFAULT ('AtStation'),
    CONSTRAINT PK_TraceabilitySerialReservations PRIMARY KEY (SiteId, SerialNumber),
    CONSTRAINT CK_Traceability_Quality CHECK (QualityState IN ('Pending','Released','Held','Rework','Scrapped')),
    CONSTRAINT CK_Traceability_Location CHECK (LocationState IN ('AtStation','InTransit','AtRack','Shipped'))
);

IF OBJECT_ID('traceability.DuplicateSerialIncidents') IS NULL
CREATE TABLE traceability.DuplicateSerialIncidents (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SubmissionId nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    SerialNumber varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
    EventId uniqueidentifier NOT NULL,
    ActorId nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    OccurredAt datetimeoffset(7) NOT NULL,
    CONSTRAINT PK_TraceabilityDuplicateSerialIncidents PRIMARY KEY (SiteId, SubmissionId),
    CONSTRAINT UQ_TraceabilityDuplicateSerialEvent UNIQUE (SiteId, EventId),
    CONSTRAINT FK_TraceabilityDuplicateSerialReservation FOREIGN KEY (SiteId, SerialNumber)
        REFERENCES traceability.SerialReservations (SiteId, SerialNumber)
);

IF OBJECT_ID('traceability.ActorRoles') IS NULL
CREATE TABLE traceability.ActorRoles (
    SiteId varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ActorId nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    RoleCode nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CONSTRAINT PK_TraceabilityActorRoles PRIMARY KEY (SiteId, ActorId, RoleCode)
);

IF DATABASE_PRINCIPAL_ID('nvm_app') IS NOT NULL
BEGIN
    GRANT SELECT ON traceability.Routes TO nvm_app;
    DENY INSERT, UPDATE, DELETE ON traceability.Routes TO nvm_app;
    GRANT SELECT, INSERT, UPDATE ON traceability.SerialReservations TO nvm_app;
    DENY DELETE ON traceability.SerialReservations TO nvm_app;
    GRANT SELECT, INSERT ON traceability.DuplicateSerialIncidents TO nvm_app;
    DENY UPDATE, DELETE ON traceability.DuplicateSerialIncidents TO nvm_app;
    GRANT SELECT ON traceability.ActorRoles TO nvm_app;
    DENY INSERT, UPDATE, DELETE ON traceability.ActorRoles TO nvm_app;
END;
