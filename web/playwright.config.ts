import { defineConfig } from "@playwright/test";

/**
 * Lab giao diện (scope v2 §5.6): chạy trên stack đang chạy thật (make up, update-db, master-data, mom, web), không tự
 * dựng server. Dùng Edge đã cài trên máy (channel msedge) nên không phải tải trình duyệt riêng.
 */
export default defineConfig({
  testDir: "./e2e",
  timeout: 60_000,
  fullyParallel: false,
  workers: 1,
  reporter: [["list"]],
  use: {
    baseURL: `http://localhost:${process.env.NVM_PORT_WEB ?? "3100"}`,
    channel: process.env.NVM_LAB_BROWSER_CHANNEL ?? "msedge",
    locale: "vi-VN",
    trace: "retain-on-failure",
  },
});
