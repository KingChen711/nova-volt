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

// Cần dữ liệu tin máy trên stack: `make line-sim` (hoặc `make line-load`) với host đang chạy.
test("line lead finds a recorded serial and the lots consumed for it", async ({ page }) => {
  await signIn(page, "lead.nv1");
  await page.goto("/area/line/messages");

  await expect(page.getByRole("heading", { name: "Tin từ xưởng" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Lệnh theo ca" })).toBeVisible();
  // Một dòng của bước lắp cell (ca010), mới nhất.
  const row = page.getByRole("row").filter({ hasText: "ca010" }).first();
  await row.waitFor({ timeout: 15_000 });
  const serial = (await row.getByRole("button").innerText()).trim();
  await row.getByRole("button").click();

  await expect(page.getByText(`Nguyên liệu đã trừ cho ${serial}`)).toBeVisible();
  // Cell lắp: cuộn điện cực theo lô, màng ngăn và vỏ từ MTU tạm.
  await expect(page.getByText(/^Auto_NV-RM-SEP_/).first()).toBeVisible();
});

test("a manual message for an unknown machine is Succeeded yet not executed, and the screen shows why (lab L09)", async ({ page }) => {
  await signIn(page, "lead.nv1");
  await page.goto("/area/line/messages");

  // Trường bắt buộc có dấu * trong nhãn: tìm theo id của ô.
  await page.locator("#eq").fill("el010_m9_1");
  await page.locator("#sn").fill(`WEB-${Date.now()}`);
  await page.getByRole("button", { name: "Gửi tin" }).click();

  const body = page.locator("pre").last();
  await expect(body).toContainText('"Succeeded": true');
  await expect(body).toContainText('"NotExecutedOperations"');
  await expect(body).toContainText("has no mapping");
});
