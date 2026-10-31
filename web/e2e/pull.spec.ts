import { readFileSync } from "node:fs";
import { join } from "node:path";
import { expect, test, type Browser, type Page } from "@playwright/test";

const root = join(__dirname, "..", "..");
const mom = `http://localhost:${process.env.NVM_PORT_MOM ?? "5080"}/mom/NV1/Application`;

function env(name: string): string {
  const line = readFileSync(join(root, ".env"), "utf8").split(/\r?\n/).find((l) => l.startsWith(`${name}=`));
  const value = line?.slice(name.length + 1).trim().replace(/^"|"$/g, "");
  if (!value) {
    throw new Error(`${name} is not set in .env`);
  }

  return value;
}

async function signIn(page: Page, user: string) {
  await page.goto("/");
  await page.getByRole("button", { name: "Đăng nhập bằng Keycloak", exact: true }).click();
  await page.locator("#username").fill(user);
  await page.locator("#password").fill(env("NVM_DEMO_USER_PASSWORD"));
  await page.locator("#password").press("Enter");
  await page.waitForURL("**/dashboard");
}

/** Mỗi vai một phiên trình duyệt riêng. */
async function asUser(browser: Browser, user: string): Promise<Page> {
  const page = await (await browser.newContext({ baseURL: `http://localhost:${process.env.NVM_PORT_WEB ?? "3100"}`, locale: "vi-VN" })).newPage();
  await signIn(page, user);
  return page;
}

async function adminToken(): Promise<string> {
  const response = await fetch(`http://localhost:${env("NVM_PORT_KEYCLOAK")}/realms/novavolt/protocol/openid-connect/token`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ grant_type: "password", client_id: "nvm-cli", username: "admin.nv1", password: env("NVM_DEMO_USER_PASSWORD") }),
  });
  return ((await response.json()) as { access_token: string }).access_token;
}

async function command(token: string, app: string, name: string, body: unknown) {
  const response = await fetch(`${mom}/${app}/odata/${name}`, {
    method: "POST",
    headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  expect(response.ok, await response.clone().text()).toBeTruthy();
}

async function read<T>(token: string, resource: string): Promise<T[]> {
  const response = await fetch(`${mom}/PullApp/odata/${resource}`, { headers: { Authorization: `Bearer ${token}` } });
  return ((await response.json()) as { value: T[] }).value;
}

function directLabel(po: string, batch: string, quantity: string): string {
  const cell = (value: string, width: number) => value.padEnd(width).slice(0, width);
  return `${cell("DEL", 10)}${cell(po, 10)}${cell("NV-RM-SEP", 14)}${cell(batch, 10)}${quantity}STO`;
}

// Việc chở còn mở từ lượt lab trước làm lô cũ chiếm FEFO: lab tự dọn việc của route và lô của nó bằng lệnh hủy, gỡ.
async function clearOpen(token: string, route: string) {
  for (const level of ["4", "3", "2"]) {
    const tasks = await read<{ TaskNId: string; RouteNId: string }>(token, `PullTasks(Area='MOD-L1',Level='${level}')`);
    for (const t of tasks.filter((x) => x.RouteNId === route)) {
      await fetch(`${mom}/PullApp/odata/CancelPullTask`, { method: "POST", headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" }, body: JSON.stringify({ TaskNId: t.TaskNId }) });
    }
  }
}

// Cần trên stack: suite R10 đã nạp, host và web đang chạy.
test.describe.serial("material pull", () => {
  const batch = `B${Date.now().toString(36).toUpperCase()}`.slice(0, 10);
  const lot = `${batch}_1`;
  const label = directLabel(`P${batch}`.slice(0, 10), batch, "010.000");

  test("direct material is received with its expiry, called from the line, moved tier by tier, and L20 refuses the load", async ({ browser }) => {
    let page: Page;
    const token = await adminToken();
    await clearOpen(token, "RT-SEP");

    // Lô cũ của lượt trước ở các ô của route: gỡ để FEFO chỉ còn lô của lượt này.
    for (const buffer of ["RCV-R02", "PC-T2-03"]) {
      const held = await read<{ NId: string; MaterialNId?: string }>(token, `BufferAssignment?$filter=BufferNId eq '${buffer}'`);
      for (const row of held) {
        await fetch(`${mom}/Production/odata/DetachMtuFromBuffer`, { method: "POST", headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" }, body: JSON.stringify({ MtuNId: row.NId }) });
      }
    }

    await command(token, "PullApp", "ReceiveDirectDelivery", { Payload: JSON.stringify([{ poNo: `P${batch}`.slice(0, 10), poLine: "10", material: "NV-RM-SEP", batch, quantity: 10, expirationDate: "20270101" }]) });

    // Nhận ở ô tier 2 của route; quy đổi Receiving: 1 thành 10 m; nhận vượt bị chặn.
    page = await asUser(browser, "log.nv1");
    await page.goto("/area/andon/receive");
    await expect(page.getByRole("heading", { name: "Nhận đặt thẳng" })).toBeVisible();
    await page.locator("#dr-label").fill(label);
    await page.locator("#dr-location").fill("NV1-CMN-PC-T2-03");
    await page.locator("#dr-quantity").fill("11");
    await page.getByRole("button", { name: "Nhận", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText("over receive");
    await page.locator("#dr-quantity").fill("6");
    await page.getByRole("button", { name: "Nhận", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText(lot);

    // Xưởng gọi hàng ở khu; ô đặt thẳng không có kanban.
    page = await asUser(browser, "op.nv1");
    await page.goto("/area/line/pull");
    await expect(page.getByRole("heading", { name: "Gọi hàng" })).toBeVisible();
    await page.getByRole("button", { name: /NV-RM-SEP · đặt thẳng/ }).click();
    await expect(page.locator("pre").first()).toContainText("PR-");
    const row = page.getByRole("row").filter({ hasText: "PC-T4-03" }).first();
    await expect(row).toContainText("Released");

    // Người chở: quét, ghi lô, hoàn tất; lô sang ô giao và có việc bù chặng dưới.
    page = await asUser(browser, "log.nv1");
    await page.goto("/area/andon/tasks");
    await expect(page.getByRole("heading", { name: "Việc chở" })).toBeVisible();
    const taskRow = page.getByRole("row").filter({ hasText: "NV-RM-SEP" }).first();
    await taskRow.getByRole("button").first().click();
    await page.getByRole("button", { name: "Hoàn tất", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText("no recorded lot");
    await page.locator("#tk-label").fill(label);
    await page.getByRole("button", { name: "Quét", exact: true }).click();
    await expect(page.getByText(lot, { exact: true })).toBeVisible();
    await expect(page.getByText("Đúng nguồn")).toBeVisible();
    await page.getByRole("button", { name: "Ghi lô", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText('"Succeeded": true');
    await expect(taskRow).toContainText("MaterialDelivered");
    await page.getByRole("button", { name: "Hoàn tất", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText('"Status": "Complete"');
    await page.getByRole("button", { name: "Tới tier 2", exact: true }).click();
    await expect(page.getByRole("row").filter({ hasText: "NV-RM-SEP" }).first()).toContainText("RCV-R02");

    // Lab L20: lô còn thuộc ô giao nên nạp bị từ chối, lỗi chỉ đúng buffer; gỡ rồi nạp được.
    page = await asUser(browser, "op.nv1");
    await page.goto("/area/line/load");
    await expect(page.getByRole("heading", { name: "Nạp lô" })).toBeVisible();
    await page.locator("#ld-label").fill(lot);
    await page.getByRole("button", { name: "Tìm lô", exact: true }).click();
    const found = page.getByRole("row").filter({ hasText: lot });
    await found.getByRole("button", { name: "Nạp", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText("already linked to buffer PC-T4-03");
    await found.getByRole("button", { name: "Gỡ khỏi buffer", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText('"Succeeded": true');
    await found.getByRole("button", { name: "Nạp", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText("NV1-B1-L1-MOD-ASSY_Buffer");
    await expect(page.getByRole("row").filter({ hasText: lot }).last()).toContainText("NV-RM-SEP");
  });

  test("the board shows kanban calls and marks every row from the selected one", async ({ page }) => {
    const token = await adminToken();
    await clearOpen(token, "RT-CASE-A");
    const address = "NV-RM-CASE-A|NV1-CMN-RCV-R01";
    await command(token, "PullApp", "CallMaterial", { RouteNId: "RT-CASE-A", MaterialNId: "NV-RM-CASE-A", Quantity: 300, PartAddressNId: address });
    await command(token, "PullApp", "CallMaterial", { RouteNId: "RT-CASE-A", MaterialNId: "NV-RM-CASE-A", Quantity: 300, PartAddressNId: address, Hot: true });
    const open = await read<{ TaskNId: string; RouteNId: string; Priority: number | null }>(token, "AndonRoutes()");
    const hot = open.filter((r) => r.RouteNId === "RT-CASE-A" && r.Priority === 1).at(-1)!;
    const plain = open.filter((r) => r.RouteNId === "RT-CASE-A" && r.Priority === 2).at(-1)!;

    await signIn(page, "log.nv1");
    await page.goto("/area/andon");
    await expect(page.getByRole("heading", { name: "Bảng gọi hàng" })).toBeVisible();

    await page.getByRole("button", { name: plain.TaskNId, exact: true }).click();
    const detail = page.getByRole("region").or(page.locator("body"));
    await expect(detail.getByRole("row").filter({ hasText: plain.TaskNId }).last()).toContainText("OnTime");

    await page.getByRole("button", { name: hot.TaskNId, exact: true }).click();
    await expect(detail.getByRole("row").filter({ hasText: hot.TaskNId }).last()).toContainText("HotCall");
    await expect(detail.getByRole("row").filter({ hasText: plain.TaskNId }).last()).not.toContainText("OnTime");

    await clearOpen(token, "RT-CASE-A");
  });
});
