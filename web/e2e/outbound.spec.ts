import { readFileSync } from "node:fs";
import { join } from "node:path";
import { expect, test } from "@playwright/test";

const root = join(__dirname, "..", "..");

function env(name: string): string {
  const line = readFileSync(join(root, ".env"), "utf8").split(/\r?\n/).find((l) => l.startsWith(`${name}=`));
  const value = line?.slice(name.length + 1).trim().replace(/^"|"$/g, "");
  if (!value) {
    throw new Error(`${name} is not set in .env`);
  }

  return value;
}

// Cần trên stack: suite R11 đã nạp, host và web đang chạy. Khứ hồi với gateway và lab L21 nằm ở GatewayTests.
test("admin reads the blocks of each message class where a missing ERP message is diagnosed", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Đăng nhập bằng Keycloak", exact: true }).click();
  await page.locator("#username").fill("admin.nv1");
  await page.locator("#password").fill(env("NVM_DEMO_USER_PASSWORD"));
  await page.locator("#password").press("Enter");
  await page.waitForURL("**/dashboard");

  await page.goto("/admin/outbound");
  await expect(page.getByRole("heading", { name: "Tin ra ngoài" })).toBeVisible();
  const row = page.getByRole("row").filter({ hasText: "Production" });
  await expect(row).toContainText("Ngày 1–4");
  await expect(row).toContainText("Lần 3, Sunday");
});
