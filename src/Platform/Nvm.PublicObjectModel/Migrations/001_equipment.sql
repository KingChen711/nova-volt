CREATE SCHEMA IF NOT EXISTS pom;

CREATE TABLE pom.equipment (
    id varchar(64) PRIMARY KEY,
    site_id varchar(3) NOT NULL,
    equipment_path varchar(256) NOT NULL,
    name varchar(128) NOT NULL,
    line varchar(2) NOT NULL,
    resource varchar(32) NOT NULL,
    revision integer NOT NULL
);

CREATE INDEX ix_equipment_site_id ON pom.equipment (site_id, id);
