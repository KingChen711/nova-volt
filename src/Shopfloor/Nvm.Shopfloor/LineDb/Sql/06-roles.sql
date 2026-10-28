-- Role của MOM (K11): chỉ gọi được hai hàm của schema trung gian, không đọc được bảng máy hay bảng tin.
-- {mom_password} do installer thay; role là của cả cluster nên tạo hoặc đổi mật khẩu.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nvm_mom') THEN
        CREATE ROLE nvm_mom LOGIN PASSWORD '{mom_password}';
    ELSE
        ALTER ROLE nvm_mom LOGIN PASSWORD '{mom_password}';
    END IF;
END $$;

REVOKE ALL ON SCHEMA elec, cell, form, mod, relay FROM PUBLIC;
GRANT USAGE ON SCHEMA relay TO nvm_mom;
GRANT EXECUTE ON FUNCTION relay.claim(text, integer, text, timestamptz) TO nvm_mom;
GRANT EXECUTE ON FUNCTION relay.write_result(bigint, text, integer) TO nvm_mom;
