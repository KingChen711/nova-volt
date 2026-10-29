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

/** Pallet Full chưa cất đầu tiên của một part, đọc từ danh sách trên màn Cất kho. */
async function waitingPallet(page: Page, part: string): Promise<string> {
  await page.goto("/area/finished-goods");
  const item = page.getByRole("list", { name: "Pallet Full chưa cất" }).getByRole("button").filter({ hasText: part }).first();
  await item.waitFor({ timeout: 15_000 });
  return (await item.locator("span").first().innerText()).trim();
}

// Cần trên stack: master data R7, `make views`, `make sql-key`, rồi `make line-sim LINE=3 CELLS=360 PACK=1` và
// `make line-sim LINE=1 CELLS=192 PACK=1` với host đang chạy (đường poll), đợi tin xả hết.
test.describe.serial("finished goods", () => {
  let stored = "";

  test("shipping stores a family B pallet only at a store address of its part, then a FIFO record appears", async ({ page }) => {
    await signIn(page, "ship.nv1");
    stored = await waitingPallet(page, "NV-FG-MOD-B60");
    await page.getByRole("list", { name: "Pallet Full chưa cất" }).getByRole("button").filter({ hasText: stored }).click();
    await expect(page.getByText("NV-FG-MOD-B60").first()).toBeVisible();

    await page.locator("#st-location").fill("NV1-CMN-FGW-A01");
    await page.getByRole("button", { name: "Cất pallet" }).click();
    await expect(page.locator("pre").first()).toContainText("is not a store address of NV-FG-MOD-B60");

    await page.getByRole("button", { name: "NV1-CMN-FGW-B01" }).click();
    await page.getByRole("button", { name: "Cất pallet" }).click();
    await expect(page.locator("pre").first()).toContainText('"Succeeded": true');

    // Bản ghi FIFO do subscriber ghi sau response: tải lại tới khi có.
    await expect(async () => {
      await page.getByRole("button", { name: "Tải lại" }).click();
      await expect(page.getByRole("row").filter({ hasText: stored })).toContainText("NV1-CMN-FGW-B01");
    }).toPass({ timeout: 20_000 });
  });

  test("quality checks the stored pallet: no held item, five rules pass, QA Check succeeds", async ({ page }) => {
    test.skip(!stored, "needs the pallet stored by the previous test");
    await signIn(page, "qa.nv1");
    await page.goto("/area/finished-goods/checks");
    await page.locator("#ck-pallet").fill(stored);
    await page.locator("#ck-pallet").press("Enter");

    await expect(page.getByText("Kiểm xong: không thành phần nào bị giữ hay bị hủy")).toBeVisible();
    await expect(page.getByText("lệch")).toHaveCount(0);
    await page.getByRole("button", { name: "QA Check" }).click();
    await expect(page.locator("pre").last()).toContainText('"Succeeded": true');
  });

  test("yard parks a storage trailer at a dock, shipping loads an untested kit pallet and the schedule waits for the test", async ({ browser }) => {
    const barcode = `TRL-E2E-${Date.now() % 1_000_000}`;
    const yard = await (await browser.newContext()).newPage();
    await signIn(yard, "yard.nv1");
    await yard.goto("/area/yard");
    await yard.getByRole("row").nth(1).waitFor({ timeout: 10_000 }).catch(() => undefined);
    const used = await yard.getByRole("row").allInnerTexts();
    const slot = ["01", "02", "03", "04", "05", "06"].map((n) => `NV1-CMN-YARD-Y${n}`).find((s) => !used.some((r) => r.includes(s)));
    const dock = ["01", "02", "03", "04"].map((n) => `NV1-CMN-DOCK-D${n}`).find((d) => !used.some((r) => r.includes(d)));
    test.skip(!slot || !dock, "no free yard slot or dock left on this stack");

    await yard.locator("#tr-barcode").fill(barcode);
    await yard.locator("#tr-yard").fill(slot!);
    await yard.getByRole("button", { name: "Khai báo" }).click();
    await expect(yard.locator("pre")).toContainText('"Succeeded": true');
    await yard.getByRole("button", { name: "Đỗ xe rỗng" }).click();
    await expect(yard.locator("pre")).toContainText('"Status": "Empty"');

    await yard.getByRole("button", { name: barcode }).click();
    await yard.getByRole("button", { name: "Đặt loại kho" }).click();
    await expect(yard.locator("pre")).toContainText('"Succeeded": true');
    await yard.locator("#tr-dock").fill(dock!);
    await yard.getByRole("button", { name: "Gọi vào dock" }).click();
    await expect(yard.locator("pre")).toContainText('"Status": "Created"');
    for (const step of ["Accepted", "Picked", "Dropped"]) {
      await yard.getByRole("row").filter({ hasText: barcode }).getByRole("button", { name: step }).click();
      await expect(yard.locator("pre")).toContainText(`"Status": "${step}"`);
    }

    const ship = await (await browser.newContext()).newPage();
    await signIn(ship, "ship.nv1");
    const kitPallet = await waitingPallet(ship, "NV-FG-KIT-A");
    await ship.goto("/area/yard");
    await ship.getByRole("button", { name: barcode }).click();
    await ship.locator("#tr-pallet").fill(kitPallet);
    await ship.getByRole("button", { name: "Nạp", exact: true }).click();
    await expect(ship.locator("pre")).toContainText('"Status": "Tapped"');
    await ship.getByRole("button", { name: "Complete Load" }).click();
    await expect(ship.locator("pre")).toContainText('"Status": "Full"');
    await expect(ship.getByText("ToCallForTest")).toBeVisible();
  });
});
