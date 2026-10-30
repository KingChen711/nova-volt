import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { expect, test, type Page } from "@playwright/test";

const root = join(__dirname, "..", "..");

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

/** Token quản trị qua client dòng lệnh: lab đóng vai ERP và hệ của khách gửi tin vào (trước R11 chưa có gateway vào). */
async function adminToken(): Promise<string> {
  const response = await fetch(`http://localhost:${env("NVM_PORT_KEYCLOAK")}/realms/novavolt/protocol/openid-connect/token`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ grant_type: "password", client_id: "nvm-cli", username: "admin.nv1", password: env("NVM_DEMO_USER_PASSWORD") }),
  });
  return ((await response.json()) as { access_token: string }).access_token;
}

const mom = `http://localhost:${process.env.NVM_PORT_MOM ?? "5080"}/mom/NV1/Application`;

async function command(token: string, app: string, name: string, body: unknown) {
  const response = await fetch(`${mom}/${app}/odata/${name}`, {
    method: "POST",
    headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  const json = (await response.json()) as Record<string, unknown>;
  expect(response.status, JSON.stringify(json)).toBe(200);
  return json;
}

async function read<T>(token: string, app: string, resource: string): Promise<T[]> {
  const response = await fetch(`${mom}/${app}/odata/${resource}`, { headers: { Authorization: `Bearer ${token}` } });
  return ((await response.json()) as { value: T[] }).value;
}

/** Mã vạch trên file nhãn mới nhất của một mẫu (adapter máy in ghi XML vào output/outbox/LabelPrinter), như quét nhãn vừa in. */
function labels(template: string): string[] {
  const dir = join(root, "output", "outbox", "LabelPrinter");
  const file = readdirSync(dir)
    .filter((f) => f.startsWith(`${template}_`))
    .map((f) => ({ f, t: statSync(join(dir, f)).mtimeMs }))
    .sort((a, b) => b.t - a.t)[0].f;
  return [...readFileSync(join(dir, file), "utf8").matchAll(/<barCode>([^<]+)<\/barCode>/g)].map((m) => m[1]);
}

async function freeSlots(token: string, prefix: string, all: string[]) {
  const used = (await read<{ LocationNId: string | null }>(token, "YardApp", "Trailer")).map((t) => t.LocationNId);
  const moving = (await read<{ ToNId: string }>(token, "YardApp", "TrailerMovement?$filter=Status ne 'Dropped'")).map((m) => m.ToNId);
  return all.filter((s) => s.startsWith(prefix) && !used.includes(s) && !moving.includes(s));
}

async function scan(page: Page, value: string) {
  await page.locator("#sg-scan").fill(value);
  await page.locator("#sg-scan").press("Enter");
  await expect(page.locator("#sg-scan")).toHaveValue("", { timeout: 10_000 });
}

async function dropMovement(page: Page, trailer: string) {
  for (const step of ["Accepted", "Picked", "Dropped"]) {
    await page.getByRole("row").filter({ hasText: trailer }).getByRole("button", { name: step }).click();
    await expect(page.locator("pre").first()).toContainText('"Succeeded": true');
  }
}

const yards = ["01", "02", "03", "04", "05", "06"].map((n) => `NV1-CMN-YARD-Y${n}`);
const docks = ["01", "02", "03", "04"].map((n) => `NV1-CMN-DOCK-D${n}`);

/** Ngày mai (UTC) và giờ phút lúc chạy: mỗi lần chạy một nhóm đơn mới, không nhập vào nhóm của lần trước. */
function delivery() {
  const now = new Date();
  const tomorrow = new Date(now.getTime() + 86_400_000).toISOString().slice(0, 10);
  const hhmm = now.toISOString().slice(11, 16).replace(":", "");
  return { date: tomorrow, time: hhmm, stamp: `${tomorrow.replaceAll("-", "").slice(2)}${hhmm}` };
}

// Cần trên stack: master data tới R8, `make views`, `make sql-key`, host và web đang chạy; một pallet kit họ A Full sạch (vd
// `make line-sim LINE=1 CELLS=192 PACK=1`; chạy cả bộ lab thì cần thêm một pallet nữa vì finished-goods.spec dùng một), một pallet
// họ B60 Full ở ô kho, hai ô yard và hai cửa dock trống. Lab tắt kiểm đo cuối bằng key trong lúc chạy (pallet từ simulator không có
// bước đo) rồi bật lại. Phần họ B dừng ở Confirm nên xe của nó ở lại cửa dock (chưa có lệnh cho xe khách rỗng rời đi, N-R8-4).
test.describe.serial("shipping", () => {
  let token = "";
  const when = delivery();
  const orderA = `LA${when.stamp}`;
  let group = "";
  let deliveryNId = "";
  let pallet = "";
  let skid = "";
  const trailer = `TRL-LAB-${when.stamp}`;

  test.beforeAll(async () => {
    token = await adminToken();
    await command(token, "Foundation", "SetConfigurationKey", { NId: "Nv.EolvValidation", Val: "false", Type: "Boolean", Category: "FinishedGoods" });
  });

  test.afterAll(async () => {
    await command(token, "Foundation", "SetConfigurationKey", { NId: "Nv.EolvValidation", Val: "true", Type: "Boolean", Category: "FinishedGoods" });
  });

  test("family A: store, ERP order, release, pick by FIFO, print the skid label, stage the pallet", async ({ page }) => {
    const ack = await command(token, "ShipApp", "ReceiveSalesOrders", {
      Payload: JSON.stringify({
        orders: [{ orderNo: orderA, customerReference: `REF-${orderA}`, shipTo: "KLN", deliveryDate: when.date, deliveryTime: when.time, carrierCode: "NVCR",
          items: [{ item: "10", material: "NV-FG-KIT-A", quantity: 8, containerQuantity: 8, supplierCode: "NV100" }] }],
      }),
    });
    group = ((ack.Acks as { GroupNId: string; Accepted: boolean }[])[0]).GroupNId;
    expect(group).toBeTruthy();

    // Pallet kit mới nhất (từ simulator); chưa cất thì cất trên màn Cất kho, đã cất ở lần chạy trước thì dùng luôn.
    const candidates = await read<{ NId: string; EquipmentNId: string | null }>(token, "ShipApp",
      "Mtua?$filter=ContainerType eq 'Pallet' and MaterialNId eq 'NV-FG-KIT-A' and Status eq 'Full' and ParentMtuaNId eq null&$orderby=CreatedOn desc&$top=5");
    let newest: { NId: string; EquipmentNId: string | null } | undefined;
    for (const c of candidates) {
      // Pallet có thành phần bị giữ hay hủy thì picking chặn đúng luật; lab cần pallet sạch.
      if ((await read(token, "ShipApp", `PalletHeldItems(PalletNId='${c.NId}')`)).length === 0) {
        newest = c;
        break;
      }
    }

    if (!newest) {
      throw new Error("needs a clean full family A pallet: make line-sim LINE=1 CELLS=192 PACK=1");
    }

    pallet = newest.NId;
    await signIn(page, "ship.nv1");
    if (!newest.EquipmentNId?.startsWith("NV1-CMN-FGW")) {
      await page.goto("/area/finished-goods");
      await page.locator("#st-pallet").fill(pallet);
      await page.locator("#st-pallet").press("Enter");
      await page.getByRole("button", { name: "NV1-CMN-FGW-A01", exact: true }).click();
      await page.getByRole("button", { name: "Cất pallet", exact: true }).click();
      await expect(page.locator("pre").first()).toContainText('"Succeeded": true');
    }

    await page.goto("/area/finished-goods/orders");
    await page.getByRole("list", { name: "Nhóm đơn" }).getByRole("button").filter({ hasText: group }).click();
    await page.getByRole("button", { name: "Release", exact: true }).click();
    await expect(page.locator("pre").last()).toContainText('"Succeeded": true');

    // Bản ghi FIFO do subscriber ghi sau response: đợi pallet có trong đề xuất rồi chọn nó.
    await expect(async () => {
      await page.getByRole("button", { name: "Tải lại", exact: true }).click();
      await expect(page.getByRole("list", { name: "Pallet đề xuất" })).toContainText(pallet);
    }).toPass({ timeout: 20_000 });
    await page.getByRole("list", { name: "Pallet đề xuất" }).getByRole("button").filter({ hasText: pallet }).click();
    await page.getByRole("button", { name: "Lấy pallet", exact: true }).click();
    await expect(page.locator("pre").last()).toContainText('"Status": "Picked"');
    await page.getByRole("button", { name: "Đóng pick list", exact: true }).click();
    await expect(page.locator("pre").last()).toContainText('"Status": "Picked"');
    await page.getByRole("button", { name: "In nhãn skid", exact: true }).click();
    await expect(page.locator("pre").last()).toContainText('"Labels": 1');

    deliveryNId = (await read<{ DeliveryNId: string }>(token, "ShipApp", `PickListGroup?$filter=NId eq '${group}'`))[0].DeliveryNId;
    skid = labels("SkidTagTemplate")[0];
    await page.goto("/area/finished-goods/staging");
    await scan(page, skid);
    // Pallet khác pallet đã lấy: bị từ chối, nguyên câu lỗi hiện trên màn.
    await page.locator("#sg-scan").fill("NO-SUCH-PALLET");
    await page.locator("#sg-scan").press("Enter");
    await expect(page.locator("pre").last()).toContainText("is not on the pick list");
    await scan(page, pallet);
    await expect(page.locator("pre").last()).toContainText('"Status": "Staged"');
    await expect(page.getByRole("row").filter({ hasText: pallet })).toContainText("Staged");
  });

  test("yard takes the customer trailer in and docks it", async ({ page }) => {
    test.skip(!deliveryNId, "needs the staged delivery");
    const yard = (await freeSlots(token, "NV1-CMN-YARD", yards))[0];
    const dock = (await freeSlots(token, "NV1-CMN-DOCK", docks))[0];
    await signIn(page, "yard.nv1");
    await page.goto("/area/yard/customer");
    await page.locator("#ct-barcode").fill(trailer);
    await page.locator("#ct-pool").fill("BRK1");
    await page.getByRole("button", { name: "Khai xe", exact: true }).click();
    await page.locator("#ct-delivery").fill(deliveryNId);
    await page.locator("#ct-yard").fill(yard);
    await page.getByRole("button", { name: "Nhận xe vào bãi", exact: true }).click();
    // Route pool khác nơi nhận của chuyến: bị từ chối.
    await expect(page.locator("pre").first()).toContainText("serves BRK1");

    const right = `${trailer}-K`;
    await page.locator("#ct-barcode").fill(right);
    await page.locator("#ct-pool").fill("KLN");
    await page.getByRole("button", { name: "Khai xe", exact: true }).click();
    await page.getByRole("button", { name: "Nhận xe vào bãi", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText('"Status": "CustomerEmpty"');
    await page.locator("#ct-dock").fill(dock);
    await page.getByRole("button", { name: "Move In", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText('"Succeeded": true');
    await dropMovement(page, right);
    await expect(page.getByRole("row").filter({ hasText: right }).filter({ hasText: "KLN" })).toContainText(dock);
  });

  test("family A: the truck button opens loading without exceptions, confirm loads and prints the bill of lading", async ({ page }) => {
    test.skip(!deliveryNId, "needs the docked trailer");
    await signIn(page, "ship.nv1");
    await page.goto("/area/finished-goods/staging");
    await scan(page, skid);
    await page.getByRole("button", { name: "Chất xe", exact: true }).click();
    await expect(page.locator("pre").last()).toContainText('"Ready": true');
    await page.locator("#ld-seal").fill(`SEAL-${when.stamp}`);
    await page.locator("#ld-skid").fill(skid);
    await page.locator("#ld-skid").press("Enter");
    await page.getByRole("button", { name: "Confirm", exact: true }).click();
    await expect(page.locator("pre").last()).toContainText('"Status": "Loaded"');
    const loaded = (await read<{ BillOfLadingNo: string | null }>(token, "ShipApp", `Delivery?$filter=NId eq '${deliveryNId}'`))[0];
    expect(loaded.BillOfLadingNo).toMatch(/^BL\d{8}$/);
  });

  test("yard moves the loaded trailer out, prints the yard pass, the trailer departs and the order is shipped", async ({ page }) => {
    test.skip(!deliveryNId, "needs the loaded trailer");
    const right = `${trailer}-K`;
    const out = (await freeSlots(token, "NV1-CMN-YARD", ["NV1-CMN-YARD-Y04", "NV1-CMN-YARD-Y05"]))[0];
    await signIn(page, "yard.nv1");
    await page.goto("/area/yard/customer");
    await page.locator("#ct-barcode").fill(right);
    await page.locator("#ct-out").fill("NV1-CMN-YARD-Y01");
    await page.getByRole("button", { name: "Move Out", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText("is not a yard slot of route group KLN");
    await page.locator("#ct-out").fill(out);
    await page.getByRole("button", { name: "Move Out", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText('"Succeeded": true');
    await dropMovement(page, right);
    await page.getByRole("button", { name: "In yard pass", exact: true }).click();
    await expect(page.locator("#ct-pass")).toHaveValue(/^YP-/);
    await page.getByRole("button", { name: "Trailer Departed", exact: true }).click();
    await expect(page.locator("pre").first()).toContainText('"Status": "Free"');

    const shipped = (await read<{ Status: string; Name: string }>(token, "ShipApp", `PickListGroup?$filter=NId eq '${group}'`))[0];
    expect(shipped.Status).toBe("Shipped");
    expect(shipped.Name).toContain(" Shipped ");
    const empty = (await read<{ Status: string; Quantity: number; EquipmentNId: string }>(token, "ShipApp", `Mtua?$filter=NId eq '${pallet}'`))[0];
    expect([empty.Status, empty.Quantity, empty.EquipmentNId]).toEqual(["Empty", 0, "n/a"]);
  });

  test("family B: box order and ERP order, pick, labels, stage with every box, confirm stops without the customer system", async ({ page }) => {
    const customerOrder = `C${when.stamp}`.slice(0, 10);
    const orderB = `LB${when.stamp}`;
    await command(token, "ShipApp", "SetCustomerBoxOrder", {
      CustomerOrderNo: customerOrder, ShipTo: "BRK1", Dock: "Q2", SupplierCode: "NV100", CustomerKanban: "K7Q1", MaterialNId: "NV-FG-MOD-B60",
      QuantityPerBox: 1, TotalBoxes: 6, RouteCode: "ZR4K02",
    });
    const ack = await command(token, "ShipApp", "ReceiveSalesOrders", {
      Payload: JSON.stringify({
        orders: [{ orderNo: orderB, customerReference: customerOrder, shipTo: "BRK1", deliveryDate: when.date, deliveryTime: when.time,
          items: [{ item: "10", material: "NV-FG-MOD-B60", quantity: 6, containerQuantity: 6, customerOrderNo: customerOrder, dock: "Q2", supplierCode: "NV100" }] }],
      }),
    });
    const groupB = ((ack.Acks as { GroupNId: string }[])[0]).GroupNId;

    await signIn(page, "ship.nv1");
    await page.goto("/area/finished-goods/orders");
    await page.getByRole("list", { name: "Nhóm đơn" }).getByRole("button").filter({ hasText: groupB }).click();
    await expect(page.getByText("ZR4K02").first()).toBeVisible();
    await page.getByRole("button", { name: "Release", exact: true }).click();
    await expect(page.locator("pre").last()).toContainText('"Succeeded": true');
    const first = page.getByRole("list", { name: "Pallet đề xuất" }).getByRole("button").first();
    await first.waitFor({ timeout: 15_000 });
    const palletB = (await first.innerText()).split(". ")[1].split("\n")[0].trim();
    await first.click();
    await page.getByRole("button", { name: "Lấy pallet", exact: true }).click();
    await expect(page.locator("pre").last()).toContainText('"Status": "Picked"');
    await page.getByRole("button", { name: "Đóng pick list", exact: true }).click();
    await page.getByRole("button", { name: "In nhãn skid", exact: true }).click();
    await expect(page.locator("pre").last()).toContainText('"Labels": 2');
    await page.getByRole("row").filter({ hasText: `${orderB}-10` }).getByRole("button", { name: "Nhãn hộp", exact: true }).click();
    await expect(page.locator("pre").last()).toContainText('"Labels": 6');

    const sheets = labels("SkidSheetTemplate");
    const boxes = labels("BoxLabelTemplate");
    await page.goto("/area/finished-goods/staging");
    await scan(page, sheets[1]);
    await scan(page, palletB);
    for (const box of boxes) {
      await scan(page, box);
    }

    await expect(page.locator("pre").last()).toContainText('"Status": "Staged"');
    const deliveryB = (await read<{ DeliveryNId: string }>(token, "ShipApp", `PickListGroup?$filter=NId eq '${groupB}'`))[0].DeliveryNId;

    // Xe họ B vào dock (lệnh như màn Xe của khách), rồi Confirm: không có hệ của khách nên lệnh báo lỗi gửi SkidBuild, pallet giữ Staged.
    const truck = `TRL-LABB-${when.stamp}`;
    const yard = (await freeSlots(token, "NV1-CMN-YARD", yards))[0];
    const dock = (await freeSlots(token, "NV1-CMN-DOCK", docks))[0];
    await command(token, "YardApp", "CreateTrailer", { Barcode: truck, RoutePool: "BRK1" });
    await command(token, "YardApp", "ArriveCustomerTrailer", { Barcode: truck, DeliveryNId: deliveryB, LocationNId: yard });
    const move = (await command(token, "YardApp", "MoveTrailerIn", { Barcode: truck, DockNId: dock })).NId as string;
    for (const status of ["Accepted", "Picked", "Dropped"]) {
      await command(token, "YardApp", "UpdateTrailerMovement", { MovementNId: move, Status: status });
    }

    await scan(page, sheets[0]);
    await page.getByRole("button", { name: "Chất xe", exact: true }).click();
    await expect(page.locator("pre").last()).toContainText('"Ready": true');
    await page.locator("#ld-seal").fill(`SEAL-B-${when.stamp}`);
    await page.locator("#ld-skid").fill(sheets[0]);
    await page.locator("#ld-skid").press("Enter");
    await page.getByRole("button", { name: "Confirm", exact: true }).click();
    await expect(page.locator("pre").last()).toContainText("SkidBuild to CustomerShipSys failed");
    const still = (await read<{ Status: string }>(token, "ShipApp", `Mtua?$filter=NId eq '${palletB}'`))[0];
    expect(still.Status).toBe("Staged");
  });
});
