-- Site authorization for read-only clients, enforced by the server.
--
-- K3 says every table, event and query carries a site, and that the filter is enforced on the server
-- rather than trusted from the client. The Grafana datasource of C13 had the first half and not the
-- second: the dashboard's `site` variable is a value the browser sends, and the role behind it held
-- SELECT on the whole ts schema, so anyone able to open a panel editor could read another plant by
-- typing a different site. A display filter is not authorization.
--
-- The fix is a grant table plus scoped views, and it is deliberately NOT the shortcut the M3 plan
-- rules out — no site is pinned into the datasource file. The site a connection may read is a
-- property of its database role, written down in ts.site_read_grant, which the reader cannot see or
-- change. Read-only clients get SELECT on ts_scoped and nothing on ts, so there is no query they can
-- write that reaches another plant's rows.
--
-- This is not the Keycloak work of M13 and does not pretend to be: it authorizes a CONNECTION, not a
-- person, so everyone sharing the Grafana instance still shares its grant. What M13 adds on top is
-- who is asking. What this closes is that a viewer could reach a plant the deployment never gave the
-- instance at all.

CREATE TABLE ts.site_read_grant (
    role_name   TEXT NOT NULL,
    site_id     TEXT NOT NULL,
    granted_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    reason      TEXT NOT NULL,

    CONSTRAINT pk_site_read_grant PRIMARY KEY (role_name, site_id),
    CONSTRAINT ck_site_read_grant_site_id CHECK (site_id ~ '^[A-Z0-9]+$'),
    CONSTRAINT ck_site_read_grant_reason CHECK (length(btrim(reason)) > 0)
);

COMMENT ON TABLE ts.site_read_grant IS
    'Which database role may read which plant. Read-only clients have no privilege on this table: it '
    'is the thing they are being checked against, so being able to read it would be a hint and being '
    'able to write it would be the whole hole.';
COMMENT ON COLUMN ts.site_read_grant.reason IS
    'Why this connection was given this plant. A grant nobody can explain is a grant nobody can revoke.';

-- Everything a read-only client is allowed to see lives here, and it holds only views. Keeping it in
-- its own schema is what makes the grant reviewable in one line: USAGE on ts_scoped and nothing on
-- ts, rather than a list of table-by-table privileges that grows a hole the first time someone adds
-- a table and forgets.
CREATE SCHEMA ts_scoped;

COMMENT ON SCHEMA ts_scoped IS
    'Site-scoped read surface. Views run with the owner privileges, so a reader needs no rights on ts.';

-- current_user, evaluated when the view runs, is the querying role — a view is inlined into the
-- caller's query rather than executed as its owner, so this cannot be widened by connecting as
-- someone else. Verified against timescale/timescaledb:2.29.2-pg17: nvm_grafana selecting from a
-- view built this way sees one site and is refused on the base table.
--
-- security_barrier stops the planner pushing a caller's own function below this filter, which is
-- how a cheap VOLATILE function in a WHERE clause otherwise gets to see rows before they are dropped.
CREATE VIEW ts_scoped.telemetry_measurement WITH (security_barrier = true) AS
SELECT measurement.*
FROM ts.telemetry_measurement AS measurement
WHERE measurement.site_id IN (
    SELECT grant_row.site_id
    FROM ts.site_read_grant AS grant_row
    WHERE grant_row.role_name = current_user);

CREATE VIEW ts_scoped.process_signal WITH (security_barrier = true) AS
SELECT signal.*
FROM ts.process_signal AS signal
WHERE signal.site_id IN (
    SELECT grant_row.site_id
    FROM ts.site_read_grant AS grant_row
    WHERE grant_row.role_name = current_user);

CREATE VIEW ts_scoped.process_signal_1m WITH (security_barrier = true) AS
SELECT rollup.*
FROM ts.process_signal_1m AS rollup
WHERE rollup.site_id IN (
    SELECT grant_row.site_id
    FROM ts.site_read_grant AS grant_row
    WHERE grant_row.role_name = current_user);

-- What a dashboard should populate a site selector from. Reading the distinct sites out of the data
-- would list every plant that exists and let a viewer pick one they cannot read, which is a worse
-- experience than the leak it replaces: an empty graph that looks like a broken query.
CREATE VIEW ts_scoped.readable_site AS
SELECT grant_row.site_id
FROM ts.site_read_grant AS grant_row
WHERE grant_row.role_name = current_user;

COMMENT ON VIEW ts_scoped.readable_site IS
    'The plants this connection may read. A site picker offering anything else is offering a dead end.';

-- Fix a database that was already provisioned by the old grafana-db-init, which handed out SELECT on
-- everything in ts plus a default privilege that would keep doing so for tables added later. Running
-- the migration has to be enough; nobody should need to know that a compose one-shot must be re-run.
-- Dollar quoting is avoided on purpose: DbUp reads a $name$ delimiter as one of its own substitution
-- variables and refuses the script. Migration 006 already made the same trade for the same reason.
DO '
DECLARE
    reader TEXT;
BEGIN
    FOREACH reader IN ARRAY ARRAY[''nvm_grafana''] LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = reader) THEN
            CONTINUE;
        END IF;

        EXECUTE format(''REVOKE ALL ON ALL TABLES IN SCHEMA ts FROM %I'', reader);
        EXECUTE format(''REVOKE ALL ON SCHEMA ts FROM %I'', reader);
        EXECUTE format(
            ''ALTER DEFAULT PRIVILEGES IN SCHEMA ts REVOKE SELECT ON TABLES FROM %I'', reader);

        EXECUTE format(''GRANT USAGE ON SCHEMA ts_scoped TO %I'', reader);
        EXECUTE format(''GRANT SELECT ON ALL TABLES IN SCHEMA ts_scoped TO %I'', reader);
        EXECUTE format(
            ''ALTER DEFAULT PRIVILEGES IN SCHEMA ts_scoped GRANT SELECT ON TABLES TO %I'', reader);
    END LOOP;
END
';
