"use client";

import { Inbox, Lock, PackageMinus } from "lucide-react";
import { useState } from "react";
import { AreaTabs } from "@/components/AreaTabs";
import { Badge, Button, DataTable, EmptyState, Field, PageHeader, Panel, ResponsePanel, Toast, useToast } from "@/components/ui";
import { areas, visibleAreas } from "@/lib/areas";
import { useAuth } from "@/lib/auth";
import { useCommand, useEntitySet } from "@/lib/hooks";
import { quote } from "@/lib/mom";

type Container = { NId: string; Status: string | null; Quantity: number; MaterialNId: string | null; ContainerType: string | null };
type Item = { NId: string; MaterialNId: string; Status: string; ParentMtuaNId: string | null };

const app = "ShipApp";

/**
 * Gỡ hàng khỏi pallet. Gỡ pallet: mọi tote rời pallet về n/a, pallet số lượng 0, mất part, ép Empty; không kiểm NC, không đổi vị
 * trí. Gói lẻ: đóng sản phẩm thẳng vào gói (mã theo tiền tố và độ dài trong configuration key), một part, tối đa số chuẩn; sản
 * phẩm đang ở tote thì tote rời pallet.
 */
export default function RepackPage() {
  const { session } = useAuth();
  const area = areas.find((a) => a.slug === "finished-goods")!;
  const allowed = !session || visibleAreas(session.roles).includes(area);
  const toast = useToast();

  const [palletInput, setPalletInput] = useState("");
  const [pallet, setPallet] = useState("");
  const totes = useEntitySet<Container>(app, "Mtua", { $filter: `ParentMtuaNId eq ${quote(pallet || "-")}`, $orderby: "NId" });
  // Tập con OData của service layer không có "in": ghép bằng or.
  const toteFilter = totes.rows.map((t) => `ParentMtuaNId eq ${quote(t.NId)}`).join(" or ");
  const totesContent = useEntitySet<Item>(app, "Mtu", { $filter: toteFilter || "NId eq '-'" });
  const clear = useCommand(app, "UnloadTotesFromPallet");

  const [packageInput, setPackageInput] = useState("");
  const [packageId, setPackageId] = useState("");
  const [item, setItem] = useState("");
  const pkg = useEntitySet<Container>(app, "Mtua", { $filter: `NId eq ${quote(packageId || "-")}` });
  const pkgItems = useEntitySet<Item>(app, "Mtu", { $filter: `ParentMtuaNId eq ${quote(packageId || "-")}`, $orderby: "NId" });
  const open = useCommand(app, "OpenCkdPackage");
  const add = useCommand(app, "AddItemToCkdPackage");
  const remove = useCommand(app, "RemoveItemFromCkdPackage");
  const [last, setLast] = useState<"open" | "add" | "remove">("open");

  async function sendClear() {
    const result = await clear.send({ PalletNId: pallet });
    if (result?.ok) {
      toast.show(`Đã gỡ hết tote khỏi ${pallet}`);
      await totes.reload();
    }
  }

  async function sendOpen() {
    setLast("open");
    const result = await open.send({ PackageNId: packageInput.trim() });
    if (result?.ok) {
      setPackageId(packageInput.trim());
    }
  }

  async function sendItem(kind: "add" | "remove") {
    setLast(kind);
    const result = await (kind === "add" ? add : remove).send({ PackageNId: packageId, MtuNId: item.trim() });
    if (result?.ok) {
      setItem("");
      await Promise.all([pkg.reload(), pkgItems.reload()]);
    }
  }

  if (!allowed) {
    return (
      <Panel>
        <EmptyState icon={Lock} title="Role của bạn không được vào khu Thành phẩm">
          Khu này dành cho role {area.roles.join(", ")}.
        </EmptyState>
      </Panel>
    );
  }

  const packageResult = { open: open.result, add: add.result, remove: remove.result }[last];
  const current = pkg.rows[0];
  return (
    <>
      <PageHeader icon={PackageMinus} title="Gỡ pallet và gói lẻ" description="Gỡ mọi tote khỏi pallet; đóng sản phẩm thẳng vào gói lẻ" />
      <AreaTabs slug="finished-goods" label="Màn của khu Thành phẩm" />

      <div className="grid gap-5 xl:grid-cols-2">
        <Panel title="Gỡ pallet">
          <form
            className="flex items-end gap-2"
            onSubmit={(e) => {
              e.preventDefault();
              setPallet(palletInput.trim());
            }}
          >
            <Field id="cl-pallet" label="Quét pallet" required mono value={palletInput} onChange={setPalletInput} />
          </form>
          {pallet && (
            <div className="mt-4 flex flex-col gap-3">
              <DataTable
                rows={totes.rows}
                loading={totes.loading}
                empty={`Pallet ${pallet} không có tote nào`}
                emptyIcon={Inbox}
                minWidth="24rem"
                columns={[
                  { key: "tote", label: "Tote", mono: true, render: (t) => t.NId },
                  {
                    key: "item",
                    label: "Hàng trong tote",
                    mono: true,
                    render: (t) => totesContent.rows.filter((i) => i.ParentMtuaNId === t.NId).map((i) => i.NId).join(", ") || "—",
                  },
                ]}
              />
              <Button variant="danger" busy={clear.busy} disabled={totes.rows.length === 0} onClick={() => void sendClear()}>
                Gỡ {totes.rows.length} tote khỏi pallet
              </Button>
              {clear.result && <ResponsePanel result={clear.result} />}
            </div>
          )}
        </Panel>

        <Panel title="Gói lẻ">
          <form
            className="flex items-end gap-2"
            onSubmit={(e) => {
              e.preventDefault();
              void sendOpen();
            }}
          >
            <Field id="ck-package" label="Quét mã gói" required mono value={packageInput} onChange={setPackageInput} />
            <Button type="submit" variant="secondary" busy={open.busy}>
              Mở gói
            </Button>
          </form>
          {packageId && current && (
            <div className="mt-4 flex flex-col gap-3">
              <p className="flex flex-wrap items-center gap-2 text-sm">
                <span className="font-mono text-xs">{current.NId}</span>
                <Badge tone={current.Status === "Full" ? "ok" : "neutral"}>{current.Status ?? "—"}</Badge>
                <span className="text-muted">
                  {current.Quantity} sản phẩm · {current.MaterialNId ?? "chưa có part"}
                </span>
              </p>
              <form
                className="flex flex-wrap items-end gap-2"
                onSubmit={(e) => {
                  e.preventDefault();
                  void sendItem("add");
                }}
              >
                <Field id="ck-item" label="Quét sản phẩm" required mono value={item} onChange={setItem} />
                <Button type="submit" busy={add.busy} disabled={!item}>
                  Thêm
                </Button>
                <Button type="button" variant="danger" busy={remove.busy} disabled={!item} onClick={() => void sendItem("remove")}>
                  Gỡ
                </Button>
              </form>
              <ul className="flex flex-col gap-1 font-mono text-xs">
                {pkgItems.rows.map((i) => (
                  <li key={i.NId}>{i.NId}</li>
                ))}
              </ul>
            </div>
          )}
          {packageResult && (
            <div className="mt-4">
              <ResponsePanel result={packageResult} />
            </div>
          )}
        </Panel>
      </div>
      <Toast message={toast.message} onDone={toast.clear} />
    </>
  );
}
