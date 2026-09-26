-- Genealogy read model (scope §6.4, §8.2). Nguồn sự thật là event ở SQL Server; đây là bản
-- đọc tối ưu. Bảng cạnh append-only với runtime role: không UPDATE/DELETE trực tiếp. Gỡ liên kết
-- đi qua trace.unlink (một lần, có event nguồn). Rebuild chỉ chạy bằng credential chủ schema.
CREATE SCHEMA IF NOT EXISTS trace;
CREATE EXTENSION IF NOT EXISTS btree_gist;

-- Chiều cạnh = dòng vật liệu: parent là thượng nguồn (lot, cuộn, unit được lắp), child là hạ nguồn
-- (unit tiêu thụ, unit chứa). Cell → module → pack; lot → cell. Forward trace = đi xuôi chiều cạnh.
-- node type: 1=lot 2=cell 3=module 4=pack 5=roll 6=tray
-- edge kind: 1=TRANSFORMATION 2=ASSOCIATION 3=AGGREGATION 4=SPLIT_MERGE 5=CORRECTION
CREATE TABLE IF NOT EXISTS trace.genealogy_link (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    site_id text NOT NULL,
    edge_kind smallint NOT NULL CHECK (edge_kind BETWEEN 1 AND 5),
    parent_type smallint NOT NULL CHECK (parent_type BETWEEN 1 AND 6),
    parent_id text NOT NULL,
    child_type smallint NOT NULL CHECK (child_type BETWEEN 1 AND 6),
    child_id text NOT NULL,
    position text NULL,
    span numrange NULL,
    quantity numeric(18,6) NULL,
    uom text NULL,
    operation_run_id text NOT NULL,
    linked_at timestamptz NOT NULL,
    recorded_at timestamptz NOT NULL,
    unlinked_at timestamptz NULL,
    unlink_event_id uuid NULL,
    superseded_by bigint NULL REFERENCES trace.genealogy_link(id),
    source_event_id uuid NOT NULL,
    CHECK (span IS NULL OR (NOT isempty(span) AND lower(span) >= 0)),
    CHECK ((unlinked_at IS NULL) = (unlink_event_id IS NULL))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_genealogy_source_event ON trace.genealogy_link (source_event_id);
CREATE INDEX IF NOT EXISTS ix_genealogy_forward
    ON trace.genealogy_link (site_id, parent_type, parent_id) WHERE unlinked_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_genealogy_backward
    ON trace.genealogy_link (site_id, child_type, child_id) WHERE unlinked_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_genealogy_span ON trace.genealogy_link USING gist (span) WHERE span IS NOT NULL;

CREATE TABLE IF NOT EXISTS trace.roll_segment (
    site_id text NOT NULL,
    roll_id text NOT NULL,
    web_side char(1) NOT NULL CHECK (web_side IN ('A','B')),
    span numrange NOT NULL CHECK (NOT isempty(span) AND lower(span) >= 0),
    slurry_batch_id text NOT NULL,
    foil_lot_id text NOT NULL,
    recipe_version_id text NOT NULL,
    equipment_id text NOT NULL,
    recorded_at timestamptz NOT NULL,
    source_event_id uuid NOT NULL,
    -- Hai đoạn cùng cuộn, cùng mặt không được chồng lấn: DB chặn, không trông vào code review.
    CONSTRAINT ex_roll_segment_overlap EXCLUDE USING gist (site_id WITH =, roll_id WITH =, web_side WITH =, span WITH &&)
);
CREATE INDEX IF NOT EXISTS ix_roll_segment_span ON trace.roll_segment USING gist (span);

-- Closure của cạnh cấu thành sản phẩm (1, 2, 4, 5; không gồm AGGREGATION). paths đếm số đường
-- đi khác nhau để gỡ một cạnh trong DAG không xoá quan hệ còn đường khác.
CREATE TABLE IF NOT EXISTS rm.genealogy_closure (
    site_id text NOT NULL,
    ancestor_type smallint NOT NULL,
    ancestor_id text NOT NULL,
    descendant_type smallint NOT NULL,
    descendant_id text NOT NULL,
    paths bigint NOT NULL CHECK (paths > 0),
    PRIMARY KEY (site_id, ancestor_type, ancestor_id, descendant_type, descendant_id)
);
CREATE INDEX IF NOT EXISTS ix_genealogy_closure_backward
    ON rm.genealogy_closure (site_id, descendant_type, descendant_id, ancestor_type);

-- Inbox giữ thứ tự theo stream: một stream áp dụng version n chỉ khi n-1 đã áp dụng.
CREATE TABLE IF NOT EXISTS trace.inbox (
    site_id text NOT NULL,
    source_event_id uuid NOT NULL,
    stream_id text NOT NULL,
    stream_version bigint NOT NULL CHECK (stream_version > 0),
    global_sequence bigint NOT NULL CHECK (global_sequence > 0),
    fact jsonb NOT NULL,
    applied boolean NOT NULL DEFAULT false,
    PRIMARY KEY (site_id, source_event_id),
    UNIQUE (site_id, stream_id, stream_version)
);
CREATE INDEX IF NOT EXISTS ix_trace_inbox_pending ON trace.inbox (site_id, global_sequence) WHERE NOT applied;

CREATE TABLE IF NOT EXISTS trace.stream_progress (
    site_id text NOT NULL,
    stream_id text NOT NULL,
    stream_version bigint NOT NULL CHECK (stream_version > 0),
    PRIMARY KEY (site_id, stream_id)
);

-- Gỡ liên kết đúng một lần, kèm event nguồn. SECURITY DEFINER để runtime role không cần UPDATE.
CREATE OR REPLACE FUNCTION trace.unlink(p_link_id bigint, p_unlinked_at timestamptz, p_event_id uuid,
    p_superseded_by bigint DEFAULT NULL)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, trace AS $$
BEGIN
    UPDATE trace.genealogy_link
    SET unlinked_at = p_unlinked_at, unlink_event_id = p_event_id,
        superseded_by = coalesce(p_superseded_by, superseded_by)
    WHERE id = p_link_id AND unlinked_at IS NULL;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'genealogy link % is not active', p_link_id USING ERRCODE = 'P0002';
    END IF;
END $$;

-- Chỉ chủ schema xoá được read model của một site để rebuild.
CREATE OR REPLACE FUNCTION trace.reset_site(p_site text)
RETURNS void LANGUAGE sql AS $$
    DELETE FROM rm.genealogy_closure WHERE site_id = p_site;
    DELETE FROM trace.roll_segment WHERE site_id = p_site;
    UPDATE trace.genealogy_link SET superseded_by = NULL WHERE site_id = p_site;
    DELETE FROM trace.genealogy_link WHERE site_id = p_site;
    DELETE FROM trace.stream_progress WHERE site_id = p_site;
    UPDATE trace.inbox SET applied = false WHERE site_id = p_site;
$$;
REVOKE ALL ON FUNCTION trace.reset_site(text) FROM PUBLIC;

DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nvm_projection') THEN
        GRANT USAGE ON SCHEMA trace TO nvm_projection;
        GRANT SELECT, INSERT ON trace.genealogy_link, trace.roll_segment TO nvm_projection;
        REVOKE UPDATE, DELETE ON trace.genealogy_link, trace.roll_segment FROM nvm_projection;
        GRANT SELECT, INSERT, UPDATE, DELETE ON rm.genealogy_closure TO nvm_projection;
        GRANT SELECT, INSERT ON trace.inbox TO nvm_projection;
        GRANT UPDATE (applied) ON trace.inbox TO nvm_projection;
        GRANT SELECT, INSERT, UPDATE ON trace.stream_progress TO nvm_projection;
        GRANT EXECUTE ON FUNCTION trace.unlink(bigint, timestamptz, uuid, bigint) TO nvm_projection;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nvm_pom') THEN
        GRANT USAGE ON SCHEMA trace, rm TO nvm_pom;
        GRANT SELECT ON trace.genealogy_link, trace.roll_segment, rm.genealogy_closure TO nvm_pom;
    END IF;
END $$;
REVOKE EXECUTE ON FUNCTION trace.unlink(bigint, timestamptz, uuid, bigint) FROM PUBLIC;
