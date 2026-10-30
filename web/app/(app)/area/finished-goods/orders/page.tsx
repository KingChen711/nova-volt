"use client";

import { ClipboardList, Inbox, Lock } from "lucide-react";
import { useState } from "react";
import { AreaTabs } from "@/components/AreaTabs";
import { Badge, Button, DataTable, EmptyState, Field, PageHeader, Panel, RefreshButton, ResponsePanel, Toast, useToast } from "@/components/ui";
import { areas, visibleAreas } from "@/lib/areas";
import { useAuth } from "@/lib/auth";
import { useCommand, useEntitySet } from "@/lib/hooks";
import { quote } from "@/lib/mom";

type Group = { NId: string; Name: string | null; ShipTo: string | null; Route: string | null; DeliverOn: string | null; Status: string | null; DeliveryNId: string | null };
type Order = { NId: string; CustomerReference: string | null; Family: string | null; Status: string | null; FifoPicked: boolean };
type Line = {
  NId: string;
  OrderNId: string;
  MaterialNId: string;
  Quantity: number;
  ContainerQuantity: number;
  CustomerOrderNo: string | null;
  Dock: string | null;
  PickedQuantity: number;
  StagedQuantity: number;
  ShippedQuantity: number;
};
type Picked = { NId: string; PalletNId: string; OrderLineNId: string | null; SuggestedPalletNId: string | null; Quantity: number; Status: string | null };
type Suggested = { PalletNId: string; MaterialNId: string | null; LocationNId: string | null; BuildDate: string | null; Quantity: number };

const app = "ShipApp";

function time(value: string | null) {
  return value ? new Date(value).toISOString().replace("T", " ").slice(0, 16) + "Z" : "—";
}

function tone(status: string | null) {
  return status === "Shipped" ? "neutral" : status === "New" ? "warn" : "ok";
}

/**
 * Đơn bán và picking: nhóm đơn (mặc định ẩn nhóm đã giao), dòng đơn, Release, đề xuất FIFO, quét pallet, bỏ pick, đóng pick list,
 * in nhãn skid và nhãn hộp. Lấy lệch đề xuất không bị chặn: dòng đã lấy có chấm đỏ khi pallet khác pallet đề xuất.
 */
export default function OrdersPage() {
  const { session } = useAuth();
  const area = areas.find((a) => a.slug === "finished-goods")!;
  const allowed = !session || visibleAreas(session.roles).includes(area);

  const [showShipped, setShowShipped] = useState(false);
  const groups = useEntitySet<Group>(app, "PickListGroup", {
    $filter: showShipped ? undefined : "Status ne 'Shipped'",
    $orderby: "DeliverOn desc",
    $top: "50",
  });
  const [selected, setSelected] = useState("");
  const group = groups.rows.find((g) => g.NId === selected);
  const orders = useEntitySet<Order>(app, "SalesOrder", { $filter: `GroupNId eq ${quote(selected || "-")}`, $orderby: "NId" });
  const lines = useEntitySet<Line>(app, "SalesOrderLine", {
    $filter: orders.rows.length ? orders.rows.map((o) => `OrderNId eq ${quote(o.NId)}`).join(" or ") : "OrderNId eq '-'",
    $orderby: "NId",
  });
  const picked = useEntitySet<Picked>(app, "PickedPallet", { $filter: `GroupNId eq ${quote(selected || "-")}`, $orderby: "CreatedOn" });
  const suggested = useEntitySet<Suggested>(app, `GroupSuggestedPallets(GroupNId=${quote(selected || "-")})`, {});

  const release = useCommand(app, "ReleasePickListGroup");
  const pick = useCommand(app, "PickPallet");
  const unpick = useCommand(app, "UnpickPallet");
  const complete = useCommand(app, "CompletePickList");
  const skidLabels = useCommand(app, "PrintSkidLabels");
  const boxLabels = useCommand(app, "PrintBoxLabels");
  const [pallet, setPallet] = useState("");
  const [last, setLast] = useState<typeof release.result>(null);
  const toast = useToast();

  async function refresh() {
    await Promise.all([groups.reload(), orders.reload(), lines.reload(), picked.reload(), suggested.reload()]);
  }

  async function run(command: { send: (body: unknown) => Promise<typeof release.result> }, body: unknown, message: string) {
    const result = await command.send(body);
    setLast(result);
    if (result?.ok) {
      toast.show(message);
      await refresh();
    }

    return result;
  }

  async function completePickList() {
    const needed = lines.rows.reduce((s, l) => s + l.Quantity, 0);
    const done = lines.rows.reduce((s, l) => s + l.PickedQuantity, 0);
    const acceptShort = done < needed && window.confirm(`Mới lấy ${done}/${needed}. Đóng pick list khi chưa đủ?`);
    if (done < needed && !acceptShort) {
      return;
    }

    await run(complete, { GroupNId: selected, AcceptShort: acceptShort }, `Đã đóng pick list ${group?.Name}`);
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

  return (
    <>
      <PageHeader icon={ClipboardList} title="Đơn và picking" description="Nhóm đơn theo nơi nhận, route, giờ giao; lấy pallet theo FIFO">
        <RefreshButton busy={groups.loading} onClick={() => void refresh()} />
      </PageHeader>
      <AreaTabs slug="finished-goods" label="Màn của khu Thành phẩm" />

      <div className="grid gap-5 xl:grid-cols-[minmax(0,26rem)_minmax(0,1fr)]">
        <Panel
          flush
          title={<span className="flex items-center gap-2">Nhóm đơn <Badge>{groups.rows.length}</Badge></span>}
          actions={
            <label className="flex items-center gap-2 text-xs text-muted">
              <input type="checkbox" checked={showShipped} onChange={(e) => setShowShipped(e.target.checked)} />
              Hiện nhóm đã giao
            </label>
          }
        >
          {groups.error ? (
            <div className="p-4">
              <ResponsePanel result={groups.error} />
            </div>
          ) : (
            <ul aria-label="Nhóm đơn" className="flex flex-col">
              {groups.rows.length === 0 && <li className="p-4 text-sm text-muted">Chưa có nhóm đơn nào.</li>}
              {groups.rows.map((g) => (
                <li key={g.NId}>
                  <button
                    type="button"
                    onClick={() => setSelected(g.NId)}
                    aria-current={g.NId === selected ? "true" : undefined}
                    className={`flex w-full cursor-pointer flex-col gap-1 border-b border-line px-4 py-3 text-left hover:bg-raised ${g.NId === selected ? "bg-raised" : ""}`}
                  >
                    <span className="flex items-center justify-between gap-2">
                      <span className="font-mono text-xs">{g.Name}</span>
                      <Badge tone={tone(g.Status)}>{g.Status ?? "—"}</Badge>
                    </span>
                    <span className="text-xs text-muted">
                      {g.NId} · chuyến {g.DeliveryNId} · giao {time(g.DeliverOn)}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </Panel>

        <div className="flex flex-col gap-5">
          {!group ? (
            <Panel>
              <EmptyState icon={Inbox} title="Chọn một nhóm đơn">Pick list, đề xuất FIFO và nhãn của nhóm hiện ở đây.</EmptyState>
            </Panel>
          ) : (
            <>
              <Panel
                flush
                title={<span className="flex items-center gap-2">Pick list <Badge tone={tone(group.Status)}>{group.Status}</Badge></span>}
                actions={
                  <div className="flex flex-wrap gap-2">
                    <Button variant="secondary" busy={release.busy} disabled={group.Status !== "New"} onClick={() => void run(release, { GroupNId: selected }, "Đã Release")}>
                      Release
                    </Button>
                    <Button variant="secondary" busy={skidLabels.busy} onClick={() => void run(skidLabels, { GroupNId: selected }, "Đã in nhãn skid")}>
                      In nhãn skid
                    </Button>
                  </div>
                }
              >
                <DataTable
                  rows={lines.rows}
                  loading={lines.loading}
                  empty="Nhóm không có dòng đơn"
                  emptyIcon={Inbox}
                  minWidth="52rem"
                  columns={[
                    { key: "line", label: "Dòng", mono: true, render: (l) => l.NId },
                    { key: "ref", label: "Đơn khách", mono: true, render: (l) => l.CustomerOrderNo ?? orders.rows.find((o) => o.NId === l.OrderNId)?.CustomerReference ?? "—" },
                    { key: "part", label: "Part", mono: true, render: (l) => l.MaterialNId },
                    { key: "qty", label: "Cần", mono: true, render: (l) => l.Quantity },
                    { key: "picked", label: "Đã lấy", mono: true, render: (l) => l.PickedQuantity },
                    { key: "staged", label: "Staged", mono: true, render: (l) => l.StagedQuantity },
                    { key: "shipped", label: "Đã giao", mono: true, render: (l) => l.ShippedQuantity },
                    {
                      key: "fifo",
                      label: "FIFO",
                      render: (l) => {
                        const order = orders.rows.find((o) => o.NId === l.OrderNId);
                        return <Badge tone={order?.FifoPicked === false ? "warn" : "ok"}>{order?.FifoPicked === false ? "lệch" : "đúng"}</Badge>;
                      },
                    },
                    {
                      key: "box",
                      label: "",
                      render: (l) =>
                        orders.rows.find((o) => o.NId === l.OrderNId)?.Family === "B" ? (
                          <Button variant="ghost" onClick={() => void run(boxLabels, { OrderLineNId: l.NId }, `Đã in nhãn hộp ${l.NId}`)}>
                            Nhãn hộp
                          </Button>
                        ) : null,
                    },
                  ]}
                />
              </Panel>

              <div className="grid gap-5 lg:grid-cols-2">
                <Panel title="Đề xuất theo FIFO">
                  {suggested.error ? (
                    <ResponsePanel result={suggested.error} />
                  ) : suggested.rows.length === 0 ? (
                    <p className="text-sm text-muted">Không có pallet Full ở ô kho của part còn thiếu.</p>
                  ) : (
                    <ol aria-label="Pallet đề xuất" className="flex flex-col gap-1">
                      {suggested.rows.map((s, i) => (
                        <li key={`${s.PalletNId}-${i}`}>
                          <button
                            type="button"
                            onClick={() => setPallet(s.PalletNId)}
                            className="flex w-full cursor-pointer justify-between gap-2 rounded-md px-2 py-1 text-left font-mono text-xs hover:bg-raised"
                          >
                            <span>
                              {i + 1}. {s.PalletNId}
                            </span>
                            <span className="text-muted">
                              {s.LocationNId} · {time(s.BuildDate)}
                            </span>
                          </button>
                        </li>
                      ))}
                    </ol>
                  )}
                </Panel>

                <Panel title="Lấy pallet">
                  <form
                    className="flex flex-col gap-3"
                    onSubmit={(e) => {
                      e.preventDefault();
                      void run(pick, { GroupNId: selected, PalletNId: pallet.trim() }, `Đã lấy ${pallet}`).then((r) => r?.ok && setPallet(""));
                    }}
                  >
                    <Field id="pk-pallet" label="Quét pallet" required mono value={pallet} onChange={setPallet} hint="Enter để lấy pallet cho nhóm" />
                    <div className="flex flex-wrap gap-2">
                      <Button type="submit" busy={pick.busy} disabled={!pallet}>
                        Lấy pallet
                      </Button>
                      <Button type="button" variant="secondary" busy={complete.busy} disabled={group.Status !== "Released"} onClick={() => void completePickList()}>
                        Đóng pick list
                      </Button>
                    </div>
                  </form>
                  <ul aria-label="Pallet đã lấy" className="mt-4 flex flex-col gap-1">
                    {picked.rows.map((p) => (
                      <li key={p.NId} className="flex items-center justify-between gap-2 font-mono text-xs">
                        <span className="flex items-center gap-2">
                          <span
                            aria-label={p.SuggestedPalletNId && p.SuggestedPalletNId !== p.PalletNId ? "lệch đề xuất" : "đúng đề xuất"}
                            className={`inline-block size-2 rounded-full ${p.SuggestedPalletNId && p.SuggestedPalletNId !== p.PalletNId ? "bg-bad" : "bg-ok"}`}
                          />
                          {p.PalletNId} · {p.Quantity}
                        </span>
                        <span className="flex items-center gap-2">
                          <Badge>{p.Status ?? "—"}</Badge>
                          {p.Status === "Picked" && (
                            <Button variant="danger" onClick={() => void run(unpick, { GroupNId: selected, PalletNId: p.PalletNId }, `Đã bỏ pick ${p.PalletNId}`)}>
                              Bỏ pick
                            </Button>
                          )}
                        </span>
                      </li>
                    ))}
                  </ul>
                </Panel>
              </div>
            </>
          )}
          {last && <ResponsePanel result={last} />}
        </div>
      </div>
      <Toast message={toast.message} onDone={toast.clear} />
    </>
  );
}
