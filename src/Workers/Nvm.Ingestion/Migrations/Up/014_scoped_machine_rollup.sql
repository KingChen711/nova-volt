-- Migration 013 built the fast read path and left it reachable only by a role that can read schema
-- ts directly — which, after migration 008, is nobody a dashboard runs as.
--
-- Kiem tra live truoc khi sua:
--     scoped_child=<missing>
--     grafana_child_select=false
--
-- Nghia la duong 8,080 ms ton tai nhung role production khong dung duoc no ma van giu K3, va
-- dashboard van doc parent. Mot toi uu chi ai co quyen cao moi cham toi duoc thi chua phai mot toi
-- uu cua he thong — no la mot toi uu cua ban benchmark.
--
-- Cung hinh dang view voi 008: security_barrier, loc theo ts.site_read_grant qua current_user, dat
-- trong schema ts_scoped de mot dong GRANT van la mot dong doc duoc. Khong cap quyen nao tren ts.
CREATE VIEW ts_scoped.process_signal_machine_1m WITH (security_barrier = true) AS
SELECT machine.*
FROM ts.process_signal_machine_1m AS machine
WHERE machine.site_id IN (
    SELECT grant_row.site_id
    FROM ts.site_read_grant AS grant_row
    WHERE grant_row.role_name = current_user);

COMMENT ON VIEW ts_scoped.process_signal_machine_1m IS
    'Per-machine per-minute rollup, filtered to the sites the querying role is granted. The read a '
    'line-wide dashboard should make; ts_scoped.process_signal_1m stays for per-channel reads.';

-- 008 cap quyen bang mot vong lap tren cac role read-only da ton tai, va `ALTER DEFAULT PRIVILEGES`
-- cua no chi ap cho bang do CHINH role chay migration tao ra ve sau. View nay duoc tao boi cung
-- role do, nen default privilege co the da phu — nhung dua vao dieu do la dua vao mot chi tiet
-- khong hien ra o day. Cap tuong minh, va cap dung cho cac role dang co grant.
-- DO '...' voi nhay don, KHONG dollar-quote. DbUp doc `$ten$` la mot bien thay the cua no, nen mot
-- khoi `DO $grant$ ... $grant$` chet voi "Variable grant has no value defined" truoc khi Postgres
-- kip nhin thay no. Migration 008 da chon dung loi nay va day di theo.
--
-- 008 co dat `ALTER DEFAULT PRIVILEGES IN SCHEMA ts_scoped GRANT SELECT ON TABLES`, nen view tao
-- sau boi cung role co the da duoc cap quyen san. Van cap tuong minh: dua vao mot default privilege
-- dat o mot migration khac la dua vao mot chi tiet khong doc duoc tu day, va phep kiem cuoi file
-- moi la thu noi no da co hay chua.
DO '
DECLARE
    reader TEXT;
BEGIN
    FOR reader IN
        SELECT DISTINCT grant_row.role_name
        FROM ts.site_read_grant AS grant_row
        WHERE EXISTS (SELECT 1 FROM pg_roles WHERE rolname = grant_row.role_name)
    LOOP
        EXECUTE format(''GRANT SELECT ON ts_scoped.process_signal_machine_1m TO %I'', reader);
    END LOOP;
END
';
