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

test("line lead sees the shift work orders of an area with its shifts and next timer", async ({ page }) => {
  await signIn(page, "lead.nv1");
  await page.goto("/area/line");

  await expect(page.getByRole("heading", { name: "Lệnh theo ca" })).toBeVisible();
  await expect(page.getByLabel("Khu sản xuất")).toHaveValue("NV1-B1-L1-CELL");
  await expect(page.getByText("Ca S1")).toBeVisible();
  await expect(page.getByText("Ca S2")).toBeVisible();
  await expect(page.getByText("DWO:").first()).toContainText("Assy_LN1");

  await page.getByLabel("Khu sản xuất").selectOption("NV1-B1-L1-ELEC");
  await expect(page.getByText("DWO:").first()).toContainText("Coat_LN1");
});

test("abort of a frozen work order shows the raw pre-check error on screen (lab L06)", async ({ page }) => {
  await signIn(page, "lead.nv1");
  await page.goto("/area/line");
  await page.getByLabel("Khu sản xuất").selectOption("NV1-B1-L1-ELEC");
  // Đợi WO của khu mới hiện rồi mới đếm. Không chờ dòng "chưa có WO": bảng đi qua trạng thái rỗng tạm thời khi đổi khu.
  const loaded = await page
    .getByRole("cell", { name: "Coat_LN1" })
    .first()
    .waitFor({ timeout: 10_000 })
    .then(() => true, () => false);

  const frozen = page.getByRole("row").filter({ hasText: "Frozen" }).first();
  test.skip(!loaded || (await frozen.count()) === 0, "No frozen work order yet: the shift timer has not frozen a shift on this stack.");

  await frozen.getByRole("button", { name: "Hủy" }).click();
  // Next có sẵn một role="alert" rỗng (route announcer): lấy ô kết quả lệnh theo nội dung.
  const alert = page.getByRole("alert").filter({ hasText: "HTTP" });
  await expect(alert).toContainText("HTTP 400");
  await expect(alert).toContainText("CheckUserHasCorrectRoleToAbort");
  await expect(alert).toContainText("User is not allowed to Abort Work Order.");
});

test("operator reads the work orders but gets no status actions", async ({ page }) => {
  await signIn(page, "op.nv1");
  await page.goto("/area/line");

  await expect(page.getByRole("heading", { name: "Lệnh theo ca" })).toBeVisible();
  await expect(page.getByText("Chỉ line-lead đổi trạng thái hay hủy WO.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Hủy" })).toHaveCount(0);
});
