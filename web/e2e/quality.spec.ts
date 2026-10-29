import { readFileSync } from "node:fs";
import { join } from "node:path";
import { expect, test, type Page } from "@playwright/test";

/** Mật khẩu user thử đọc từ .env ở gốc repo, không in ra (cùng nguồn với make keycloak-users). */
function demoPassword(): string {
  const env = readFileSync(join(__dirname, "..", "..", ".env"), "utf8");
  const line = env.split(/\r?\n/).find((l) => l.startsWith("NVM_DEMO_USER_PASSWORD="));
  const value = line?.slice("NVM_DEMO_USER_PASSWORD=".length).trim().replace(/^"|"$/g, "");
  if (!value) {
    throw new Error("NVM_DEMO_USER_PASSWORD is not set in .env");
  }

  return value;
}

async function signIn(page: Page, user: string) {
  await page.goto("/");
  await page.getByRole("button", { name: "Đăng nhập bằng Keycloak" }).click();
  await page.locator("#username").fill(user);
  await page.locator("#password").fill(demoPassword());
  await page.locator("#password").press("Enter");
  await page.waitForURL("**/dashboard");
}

// Cần NC OPEN từ tin máy trên stack: `make quality-config`, `make views`, rồi `make line-sim` có cell KO (--ko-every) với host đang chạy.
test("quality scraps an open line NC with the five codes, then a suspect hold on that MTU shows the real reason (lab L13)", async ({ page }) => {
  await signIn(page, "qa.nv1");
  await page.goto("/area/quality");
  await expect(page.getByRole("heading", { name: "Kết luận NC" })).toBeVisible();

  // NC OPEN mới nhất có serial của cell (bước ca010).
  const row = page.getByRole("row").filter({ hasText: "ca010" }).first();
  await row.waitFor({ timeout: 15_000 });
  const serial = (await row.getByRole("cell").nth(4).innerText()).trim();
  await row.getByRole("checkbox").check();

  await page.locator("#s-creating").fill("CC-QA");
  await page.locator("#s-charge").fill("CC-L1");
  await page.locator("#s-scrap").fill("S2");
  await page.locator("#s-problem").fill("P02");
  await page.locator("#s-model").fill("STD");
  await page.getByRole("button", { name: "Lưu kết luận" }).click();
  await expect(page.locator("pre").first()).toContainText('"Succeeded": true');

  await page.locator("#h-mtu").fill(`NV-WIP-CELL-A_${serial}`);
  await page.locator("#h-defect").fill("D-LEAK");
  await page.getByRole("button", { name: "Giữ hàng" }).click();
  const body = page.locator("pre").last();
  await expect(body).toContainText("already scrapped");
  await expect(page.getByText("Màn của hệ thật chỉ báo")).toBeVisible();
});

test("bulk list with a duplicate row is rejected as a whole and the response names the row", async ({ page }) => {
  await signIn(page, "qa.nv1");
  await page.goto("/area/bulk-nc");
  await page.locator("#bulk-area").fill("NV1-B1-L1-CELL");
  await page.locator("#bulk-list").fill("SN\nNV-WIP-CELL-A_NOPE\nNV-WIP-CELL-A_NOPE");
  await page.getByRole("button", { name: "Kiểm danh sách" }).click();
  const body = page.locator("pre").last();
  await expect(body).toContainText("Duplicate row");
  await expect(body).toContainText("MTU does not exist");
});
