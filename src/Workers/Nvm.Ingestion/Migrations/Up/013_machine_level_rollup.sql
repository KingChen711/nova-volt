-- N-M3-1: mot rollup tang hai gop theo MAY, de truy van muc may thoi phai doc du lieu muc kenh.
--
-- Plan M3 section C10 da ke san don nay — "neu muc may truot 200 ms: dung rollup tang hai gop theo
-- may, do lai, va ghi CA HAI bang so" — kem dieu kien "do truoc, dung sau". Da do, ba lan, va moi
-- lan deu chi vao cung mot cho.
--
-- Do duoc, sau khi migration 011 da nen rollup:
--
--   Seq Scan on _hyper_4_123_chunk_compressed  actual rows=100
--       Rows Removed by Filter: 7900
--       Buffers: shared hit=24224
--
-- Segment exclusion CHAY DUNG: chi 100 segment cua FORM-01 duoc giai nen tren 8.000. Nhung de tim
-- ra 100 do, PostgreSQL quet tuan tu ca 8.000 dong nen, moi dong la mot mang lon. Do khong phai
-- thieu index cho DU LIEU — no la thieu duong tra cuu SEGMENT.
--
-- Va no khong sua duoc bang mot index thong thuong. Truy van muc may loc bang
-- `equipment_id LIKE '<duong dan may>/%'`, database chay collation `en_US.utf8`, va duoi collation
-- do mot btree thuong KHONG phuc vu duoc `LIKE 'prefix%'`. Da kiem: index mot cot voi
-- `text_pattern_ops` thi phuc vu duoc (`Index Cond: equipment_id ~>=~ ... AND ~<~ ...`), nhung no
-- nam tren bang GIAI NEN, con thu can tra cuu la bang NEN.
--
-- Rollup tang hai lam cau hoi doi hinh: thay vi "loc 100 kenh trong 1.000", no thanh "doc mot
-- segment cua mot may". machine_id vao segmentby, nen phep loc la mot phep BANG — thu ma segment
-- exclusion khai thac truc tiep, khong can prefix, khong phu thuoc collation.
--
-- Va no dung ve nghiep vu, khong chi dung ve ky thuat: "nhiet do trung binh cua FORM-01 phut nay"
-- la mot cau hoi ve MAY. Luu no o muc kenh roi gop lai moi lan doc la luu mot cau tra loi khac voi
-- cau hoi duoc hoi.
--
-- Trung binh CO TRONG SO: sum(avg_value * sample_count) / sum(sample_count). Trung binh cua cac
-- trung binh se sai bat cu khi nao cac kenh khong co cung so mau trong phut do — va chung khong,
-- vi duong ghi la report-by-exception.
CREATE MATERIALIZED VIEW ts.process_signal_machine_1m
WITH (timescaledb.continuous, timescaledb.materialized_only = true) AS
SELECT time_bucket(INTERVAL '1 minute', bucket) AS bucket,
       site_id,
       regexp_replace(equipment_id, '/[^/]+$', '') AS machine_id,
       signal_code,
       sum(avg_value * sample_count) / sum(sample_count) AS avg_value,
       min(min_value) AS min_value,
       max(max_value) AS max_value,
       sum(sample_count) AS sample_count
FROM ts.process_signal_1m
GROUP BY 1, 2, 3, 4
WITH NO DATA;

-- COMMENT ON VIEW, khong phai ON MATERIALIZED VIEW: mot continuous aggregate lo ra ngoai duoi dang
-- VIEW thuong dat tren materialization hypertable, nen `COMMENT ON MATERIALIZED VIEW` bao
-- 42809 "is not a materialized view". `ALTER MATERIALIZED VIEW ... SET (timescaledb.compress...)`
-- ben duoi thi lai dung cu phap materialized view — hai lenh, hai cach goi cung mot doi tuong.
COMMENT ON VIEW ts.process_signal_machine_1m IS
    'Per-minute signal averages for one machine, weighted by sample count. Reads that ask about a '
    'machine read this; reads that ask about a channel read ts.process_signal_1m.';

-- end_offset mot bucket SAU cha. Rollup nay doc tu ts.process_signal_1m, nen refresh no som hon cha
-- se materialize mot phut ma cha chua chot — va khong co gi bao lai sau do, dung hinh dang loi ma
-- D5 va ADR-032 ton tai de chan.
SELECT add_continuous_aggregate_policy(
    'ts.process_signal_machine_1m',
    start_offset => INTERVAL '5 hours',
    end_offset => INTERVAL '2 minutes',
    schedule_interval => INTERVAL '1 minute');

-- Cung segmentby, cung ly do o migration 011: mot segment mot chuoi. Khac o chO machine_id thay
-- equipment_id, va do chinh la diem — mot may la mot segment, nen doc mot may la doc mot segment.
ALTER MATERIALIZED VIEW ts.process_signal_machine_1m SET (
    timescaledb.compress = true,
    timescaledb.compress_segmentby = 'site_id, machine_id, signal_code',
    timescaledb.compress_orderby = 'bucket DESC'
);

SELECT add_compression_policy(
    'ts.process_signal_machine_1m',
    compress_after => INTERVAL '7 days');
