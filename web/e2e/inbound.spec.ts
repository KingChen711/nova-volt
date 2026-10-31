import { readFileSync } from "node:fs";
import { join } from "node:path";
import { expect, test, type Page } from "@playwright/test";

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

async function adminToken(): Promise<string> {
  const response = await fetch(`http://localhost:${env("NVM_PORT_KEYCLOAK")}/realms/novavolt/protocol/openid-connect/token`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ grant_type: "password", client_id: "nvm-cli", username: "admin.nv1", password: env("NVM_DEMO_USER_PASSWORD") }),
  });
  return ((await response.json()) as { access_token: string }).access_token;
}

async function read<T>(token: string, resource: string): Promise<T[]> {
  const response = await fetch(`${mom}/YardApp/odata/${resource}`, { headers: { Authorization: `Bearer ${token}` } });
  return ((await response.json()) as { value: T[] }).value;
}

async function command(token: string, name: string, body: unknown) {
  const response = await fetch(`${mom}/YardApp/odata/${name}`, {
    method: "POST",
    headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  expect(response.ok, await response.clone().text()).toBeTruthy();
}

/** Ô trống đầu tiên trong dải vị trí: stack dùng lâu còn xe của lượt lab trước. */
async function free(token: string, prefix: string, count: number, skip = 0): Promise<string> {
  const used = new Set((await read<{ LocationNId: string | null }>(token, "Trailer?$top=500")).map((t) => t.LocationNId));
  const open = new Set((await read<{ ToNId: string }>(token, "TrailerMovement?$filter=Status ne 'Dropped'")).map((m) => m.ToNId));
  const slots = Array.from({ length: count }, (_, i) => `${prefix}${String(i + 1).padStart(2, "0")}`).filter((s) => !used.has(s) && !open.has(s));
  if (!slots[skip]) {
    throw new Error(`no free slot at ${prefix}: clear test trailers on the local stack`);
  }

  return slots[skip];
}

/** Cửa thuộc logical dock L1. */
async function freeDock(token: string): Promise<string> {
  const used = new Set((await read<{ LocationNId: string | null }>(token, "Trailer?$top=500")).map((t) => t.LocationNId));
  const open = new Set((await read<{ ToNId: string }>(token, "TrailerMovement?$filter=Status ne 'Dropped'")).map((m) => m.ToNId));
  const door = ["NV1-CMN-DOCK-D01", "NV1-CMN-DOCK-D02", "NV1-CMN-DOCK-D05"].find((d) => !used.has(d) && !open.has(d));
  if (!door) {
    throw new Error("no free L1 door: clear test trailers on the local stack");
  }

  return door;
}

function label(supplier: string, dock: string, po: string, route: string, pallet: string, skid: string): string {
  const cell = (value: string, width: number) => value.padEnd(width).slice(0, width);
  return `NV01${cell(supplier, 5)}${cell(dock, 2)}${cell(po, 10)}${cell(route, 8)}${cell(pallet, 2)}${cell(skid, 3)}XSPARE`;
}

async function pull(page: Page, trailer: string) {
  await page.goto("/area/yard/dock");
  for (const step of ["Accepted", "Picked", "Dropped"]) {
    const row = page.getByRole("row").filter({ hasText: trailer }).filter({ has: page.getByRole("button", { name: step, exact: true }) });
    await row.getByRole("button", { name: step, exact: true }).click();
    await expect(page.locator("pre").first()).toContainText('"Succeeded": true');
  }
}

// Cần trên stack: suite R9 đã nạp, host và web đang chạy, hai cửa dock logical L1 và một ô yard trống.
test.describe.serial("inbound trailers", () => {
  const stamp = Date.now().toString(36).slice(-6).toUpperCase();
  const source = `TRL-IN-${stamp}`;
  const destination = `TRL-IN-${stamp}-D`;
  const route = `R${stamp}`;
  const routeB = `S${stamp}`;
  const po = `P${stamp}001`.padEnd(10, "0").slice(0, 10);
  const poB = `Q${stamp}002`.padEnd(10, "0").slice(0, 10);

  test("gate registers a component trailer from the schedule, a shunter pulls it, the dock receives a skid", async ({ page }) => {
    const token = await adminToken();
    const when = new Date(Date.now() + 3600_000).toISOString();
    const line = (skid: string, order: string) => ({ poNo: order, supplier: "SUP-1", dock: "L1", pallet: "F", skid, material: "NV-RM-CASE-A", kanban: "KB-CASE-A", quantityPerBox: 6, boxCount: 50 });
    await command(token, "ReceiveDeliverySchedule", { Payload: JSON.stringify({ route, trailerNo: source, plannedOn: when, skids: [line("001", po), line("002", po)] }) });
    await command(token, "ReceiveDeliverySchedule", { Payload: JSON.stringify({ route: routeB, trailerNo: destination, plannedOn: when, skids: [line("001", poB)] }) });
    const yardA = await free(token, "NV1-CMN-YARD-Y", 12);

    await signIn(page, "yard.nv1");
    await page.goto("/area/yard/inbound");
    await expect(page.getByRole("heading", { name: "Xe đến" })).toBeVisible();
    await page.locator("#ib-barcode").fill(source);
    await page.locator("#ib-no").fill(source);
    await page.getByRole("button", { name: "Khai trailer", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText('"Status": "Free"');

    await page.getByRole("button", { name: route, exact: true }).click();
    await page.locator("#ib-yard").fill("NV1-CMN-DOCK-D01");
    await page.getByRole("button", { name: "Đăng ký xe linh kiện", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText("is not a yard slot");
    await page.locator("#ib-yard").fill(yardA);
    await page.getByRole("button", { name: "Đăng ký xe linh kiện", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText("PL-C");

    const dock = await freeDock(token);
    await page.goto("/area/yard/dock");
    await page.getByRole("button", { name: source, exact: true }).click();
    await page.locator("#dk-destination").fill(dock);
    await page.getByRole("button", { name: "Tạo yêu cầu kéo", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText('"Status": "Created"');
    await pull(page, source);

    await page.goto("/area/yard/receive");
    await page.getByRole("button", { name: source, exact: true }).click();
    await page.locator("#rc-label").fill(label("SUP-1", "L1", po, route, "F", "001").slice(0, 39));
    await page.locator("#rc-location").fill("RC-01");
    await page.getByRole("button", { name: "Nhận skid", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText("got 39");

    await page.locator("#rc-label").fill(label("SUP-1", "L1", po, route, "F", "001"));
    await page.getByRole("button", { name: "Nhận skid", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText('"Status": "Tapped"');
    await expect(page.getByRole("row").filter({ hasText: po }).filter({ hasText: "Đã nhận" })).toHaveCount(1);
  });

  test("a received skid does not move, the other skid transfers to the second trailer and is received there", async ({ page }) => {
    const token = await adminToken();
    await signIn(page, "yard.nv1");
    await page.goto("/area/yard/inbound");
    await page.locator("#ib-barcode").fill(destination);
    await page.locator("#ib-no").fill(destination);
    await page.getByRole("button", { name: "Khai trailer", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText('"Status": "Free"');
    await page.getByRole("button", { name: routeB, exact: true }).click();
    await page.locator("#ib-yard").fill(await free(token, "NV1-CMN-YARD-Y", 12));
    await page.getByRole("button", { name: "Đăng ký xe linh kiện", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText("PL-C");

    await page.goto("/area/yard/dock");
    await page.getByRole("button", { name: destination, exact: true }).click();
    await page.locator("#dk-destination").fill(await freeDock(token));
    await page.getByRole("button", { name: "Tạo yêu cầu kéo", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText('"Status": "Created"');
    await pull(page, destination);

    await page.goto("/area/yard/transfer");
    await page.locator("#tf-reason").selectOption("Consolidate");
    await page.locator("#tf-comment").fill("gop hai xe");
    await page.locator("#tf-source").fill(source);
    await page.locator("#tf-destination").fill(destination);
    await page.locator("#tf-label").fill(label("SUP-1", "L1", po, route, "F", "001"));
    await page.getByRole("button", { name: "Kiểm nhãn", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText("No unreceived skid");

    const second = label("SUP-1", "L1", po, route, "F", "002");
    await page.locator("#tf-label").fill(second);
    await page.getByRole("button", { name: "Kiểm nhãn", exact: true }).click();
    await expect(page.getByRole("button", { name: "Lưu (1 skid)", exact: true })).toBeEnabled();
    await page.getByRole("button", { name: "Lưu (1 skid)", exact: true }).click();
    await expect(page.getByText("Đã chuyển skid", { exact: true })).toBeVisible();

    const after = await read<{ NId: string; Status: string; Route: string }>(token, `Trailer?$filter=NId eq '${source}' or NId eq '${destination}'`);
    expect(after.find((t) => t.NId === source)?.Status).toBe("Empty");
    expect(after.find((t) => t.NId === destination)?.Route).toBe([route, routeB].sort().join(","));

    await page.goto("/area/yard/receive");
    await page.getByRole("button", { name: destination, exact: true }).click();
    await page.locator("#rc-label").fill(second);
    await page.locator("#rc-location").fill("RC-01");
    await page.getByRole("button", { name: "Nhận skid", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText('"Status": "Tapped"');
  });

  test("an unplanned direct material trailer goes straight to its door and is deleted when it departs", async ({ page }) => {
    const plate = `DM${stamp}`;
    await signIn(page, "yard.nv1");
    await page.goto("/area/yard/inbound");
    await page.locator("#ib-plate").fill(plate);
    await page.locator("#ib-carrier").fill("Hang thu");
    await page.locator("#ib-door").selectOption("NV1-CMN-DOCK-D07");
    await page.getByRole("button", { name: "Đăng ký", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText('"Status": "Full"');

    await page.goto("/area/yard/dock");
    await page.getByRole("button", { name: plate, exact: true }).click();
    await page.getByRole("button", { name: "Cho xe rời", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText('"Status": "Deleted"');
    await expect(page.getByRole("button", { name: plate, exact: true })).toHaveCount(0);
  });
});
