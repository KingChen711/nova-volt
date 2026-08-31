-- Removes the site-scoped read surface.
--
-- It does NOT put the blanket ts grants back, and that asymmetry is deliberate: rolling a schema
-- change back is not a reason to hand a read-only client every plant again. A database rolled back to
-- here has a Grafana role that can reach nothing, which is a visibly broken dashboard rather than a
-- silent cross-site read.
DROP SCHEMA ts_scoped CASCADE;
DROP TABLE ts.site_read_grant;
