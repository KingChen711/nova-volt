"use client";

import { Truck } from "lucide-react";
import { useState } from "react";
import { AreaTabs } from "@/components/AreaTabs";
import { Badge, Button, DataTable, EmptyState, Field, PageHeader, Panel, RefreshButton, ResponsePanel, StatCard, Toast, useToast } from "@/components/ui";
import { areas, visibleAreas } from "@/lib/areas";
import { useAuth } from "@/lib/auth";
import { useCommand, useEntitySet } from "@/lib/hooks";
import { quote, type MomResult } from "@/lib/mom";

type Skid = { NId: string; SkidNo: number; PalletNId: string | null; Status: string | null; OrderLineNId: string };
type Delivery = { NId: string; GroupNId: string; Status: string | null; TrailerNId: string | null; ConfirmationNo: string | null; TripNo: string | null; BillOfLadingNo: string | null };
type Entry = { NId: string; OrderNId: string; CodeNId: string; Group: string | null; Dock: string | null; Comment: string | null };
type ShipmentBody = { NId?: string; Status?: string; DeliveryNId?: string; Todo?: number; Completed?: number; Warning?: string };
type CheckBody = { Ready?: boolean; MissingGroup?: string | null; OrderNId?: string; CustomerOrderNo?: string; Dock?: string; Codes?: string[]; Declared?: string[] };

const app = "ShipApp";

function body<T>(result: MomResult | null): T | null {
  return result?.ok && result.body && typeof result.body !== "string" ? (result.body as T) : null;
}

/**
 * Staging và chất xe, một ô quét như màn thật: chưa có skid đang dựng thì mã là nhãn skid; skid chưa có pallet thì mã là pallet;
 * skid họ B đã có pallet thì mã là nhãn hộp. Nút chất xe hỏi ngoại lệ còn thiếu (họ B) theo thứ tự nhóm, rồi mới mở phần chất xe.
 */
export default function StagingPage() {
  const { session } = useAuth();
  const area = areas.find((a) => a.slug === "finished-goods")!;
  const allowed = !session || visibleAreas(session.roles).includes(area);

  const [delivery, setDelivery] = useState("");
  const [skid, setSkid] = useState("");
  const [scan, setScan] = useState("");
  const [last, setLast] = useState<MomResult | null>(null);
  const skids = useEntitySet<Skid>(app, "Skid", { $filter: `DeliveryNId eq ${quote(delivery || "-")}`, $orderby: "SkidNo" });
  const deliveries = useEntitySet<Delivery>(app, "Delivery", { $filter: `NId eq ${quote(delivery || "-")}` });
  const entries = useEntitySet<Entry>(app, "LoadingExceptionEntry", { $filter: `DeliveryNId eq ${quote(delivery || "-")}`, $orderby: "CreatedOn" });
  const current = deliveries.rows[0];
  const building = skids.rows.find((s) => s.NId === skid && s.Status === "Building");

  const scanSkid = useCommand(app, "ScanSkidLabel");
  const stage = useCommand(app, "StagePallet");
  const box = useCommand(app, "ScanBoxLabel");
  const check = useCommand(app, "CheckTruckLoading");
  const declare = useCommand(app, "AddLoadingExceptionEntry");
  const confirm = useCommand(app, "ConfirmTruckLoad");
  const [ask, setAsk] = useState<CheckBody | null>(null);
  const [code, setCode] = useState("");
  const [comment, setComment] = useState("");
  const [ready, setReady] = useState(false);
  const [seal, setSeal] = useState("");
  const [loadScan, setLoadScan] = useState("");
  const [toLoad, setToLoad] = useState<string[]>([]);
  const [progress, setProgress] = useState<ShipmentBody | null>(null);
  const toast = useToast();

  async function refresh() {
    await Promise.all([skids.reload(), deliveries.reload(), entries.reload()]);
  }

  async function onScan() {
    const value = scan.trim();
    const result = !building ? await scanSkid.send({ Barcode: value }) : !building.PalletNId ? await stage.send({ SkidNId: building.NId, PalletNId: value }) : await box.send({ SkidNId: building.NId, Barcode: value });
    setLast(result);
    const shipment = body<ShipmentBody>(result);
    if (shipment) {
      setScan("");
      setProgress(shipment);
      if (shipment.DeliveryNId) {
        setDelivery(shipment.DeliveryNId);
      }

      setSkid(shipment.Status === "Building" ? (shipment.NId ?? "") : "");
      if (shipment.Warning) {
        toast.show(shipment.Warning);
      }

      await refresh();
    }
  }

  async function onTruck() {
    const result = await check.send({ DeliveryNId: delivery });
    setLast(result);
    const answer = body<CheckBody>(result);
    setAsk(answer?.MissingGroup ? answer : null);
    setCode(answer?.Codes?.[0] ?? "");
    setReady(answer?.Ready === true);
    await entries.reload();
  }

  async function onDeclare() {
    const result = await declare.send({ DeliveryNId: delivery, OrderNId: ask?.OrderNId, Code: code, Dock: ask?.Dock, Comment: comment });
    setLast(result);
    if (result?.ok) {
      setComment("");
      await onTruck();
    }
  }

  async function onConfirm() {
    const result = await confirm.send({ DeliveryNId: delivery, SealNo: seal, Skids: toLoad });
    setLast(result);
    if (result?.ok) {
      toast.show(`Đã chất ${toLoad.length} skid`);
      setToLoad([]);
      await refresh();
    }
  }

  if (!allowed) {
    return (
      <Panel>
        <EmptyState title="Role của bạn không được vào khu Thành phẩm">
          Khu này dành cho role {area.roles.join(", ")}.
        </EmptyState>
      </Panel>
    );
  }

  const step = !building ? "nhãn skid" : !building.PalletNId ? "pallet" : "nhãn hộp";
  return (
    <>
      <PageHeader title="Staging và chất xe" description="Quét nhãn skid, pallet, nhãn hộp; khai ngoại lệ; chất xe">
        <RefreshButton busy={skids.loading} onClick={() => void refresh()} />
      </PageHeader>
      <AreaTabs slug="finished-goods" label="Màn của khu Thành phẩm" />

      <div className="grid gap-5 xl:grid-cols-[26rem_minmax(0,1fr)]">
        <div className="flex flex-col gap-5">
          <Panel title="Staging">
            <form
              className="flex flex-col gap-3"
              onSubmit={(e) => {
                e.preventDefault();
                void onScan();
              }}
            >
              <Field id="sg-scan" label={`Quét ${step}`} required mono value={scan} onChange={setScan} hint={building ? `Skid đang dựng: ${building.SkidNo.toString().padStart(3, "0")}` : "Enter để đọc mã"} />
              <Button type="submit" busy={scanSkid.busy || stage.busy || box.busy} disabled={!scan}>
                Quét
              </Button>
            </form>
            {delivery && (
              <div className="mt-4 grid grid-cols-2 gap-3">
                <StatCard label="Todo" value={progress?.Todo ?? 0} />
                <StatCard label="Completed" value={progress?.Completed ?? 0} />
              </div>
            )}
          </Panel>

          {delivery && (
            <Panel
              title={
                <span className="flex items-center gap-2">
                  Chuyến {delivery} <Badge tone={current?.Status === "Loaded" ? "ok" : "neutral"}>{current?.Status ?? "—"}</Badge>
                </span>
              }
              actions={
                <Button variant="secondary" busy={check.busy} onClick={() => void onTruck()}>
                  <Truck aria-hidden className="size-4" /> Chất xe
                </Button>
              }
            >
              <dl className="grid grid-cols-[8rem_1fr] gap-y-1 text-sm">
                <dt className="text-muted">Xe</dt>
                <dd className="font-mono text-xs">{current?.TrailerNId ?? "—"}</dd>
                <dt className="text-muted">Số xác nhận</dt>
                <dd className="font-mono text-xs">{current?.ConfirmationNo ?? "—"}</dd>
                <dt className="text-muted">Vận đơn</dt>
                <dd className="font-mono text-xs">{current?.BillOfLadingNo ?? "—"}</dd>
              </dl>

              {ask && (
                <form
                  aria-label="Khai ngoại lệ"
                  className="mt-4 flex flex-col gap-3 rounded-lg border border-warn/40 bg-warn-soft p-3"
                  onSubmit={(e) => {
                    e.preventDefault();
                    void onDeclare();
                  }}
                >
                  <p className="text-sm font-semibold">
                    Thiếu ngoại lệ {ask.MissingGroup}: đơn {ask.CustomerOrderNo ?? ask.OrderNId}
                    {ask.Dock ? `, dock ${ask.Dock}` : ""}
                  </p>
                  <label className="flex flex-col gap-1.5 text-sm font-medium">
                    Mã ngoại lệ
                    <select value={code} onChange={(e) => setCode(e.target.value)} className="min-h-touch rounded-lg border border-field bg-surface px-2 font-mono text-sm">
                      {ask.Codes?.map((c) => (
                        <option key={c}>{c}</option>
                      ))}
                    </select>
                  </label>
                  <Field id="ex-comment" label="Ghi chú" value={comment} onChange={setComment} />
                  <Button type="submit" busy={declare.busy} disabled={!code}>
                    Lưu ngoại lệ
                  </Button>
                </form>
              )}

              {ready && (
                <form
                  aria-label="Chất xe"
                  className="mt-4 flex flex-col gap-3"
                  onSubmit={(e) => {
                    e.preventDefault();
                    if (loadScan.trim()) {
                      setToLoad((list) => [...new Set([...list, loadScan.trim()])]);
                      setLoadScan("");
                    }
                  }}
                >
                  <Field id="ld-seal" label="Số niêm phong" required mono value={seal} onChange={setSeal} />
                  <Field id="ld-skid" label="Quét skid lên xe" mono value={loadScan} onChange={setLoadScan} hint="Enter để thêm vào danh sách" />
                  {/* Form hai ô cần nút submit thì Enter ở ô quét mới gửi form (implicit submission của HTML). */}
                  <Button type="submit" variant="secondary" disabled={!loadScan.trim()}>
                    Thêm skid
                  </Button>
                  <ul aria-label="Skid sẽ chất" className="flex flex-col gap-1 font-mono text-xs">
                    {toLoad.map((s) => (
                      <li key={s}>{s}</li>
                    ))}
                  </ul>
                  <Button type="button" busy={confirm.busy} disabled={!seal || toLoad.length === 0} onClick={() => void onConfirm()}>
                    Confirm
                  </Button>
                </form>
              )}
            </Panel>
          )}
          {last && <ResponsePanel result={last} />}
        </div>

        <div className="flex flex-col gap-5">
          <Panel flush title={<span className="flex items-center gap-2">Skid của chuyến <span className="font-normal text-muted">({skids.rows.length})</span></span>}>
            <DataTable
              rows={skids.rows}
              loading={skids.loading}
              empty="Chưa có skid: quét nhãn skid để bắt đầu"
              minWidth="36rem"
              columns={[
                { key: "no", label: "Skid", mono: true, render: (s) => s.SkidNo.toString().padStart(3, "0") },
                { key: "line", label: "Dòng đơn", mono: true, render: (s) => s.OrderLineNId },
                { key: "pallet", label: "Pallet", mono: true, render: (s) => s.PalletNId ?? "—" },
                { key: "status", label: "Trạng thái", render: (s) => <Badge tone={s.Status === "Staged" || s.Status === "Loaded" ? "ok" : "warn"}>{s.Status ?? "—"}</Badge> },
              ]}
            />
          </Panel>
          <Panel flush title={<span className="flex items-center gap-2">Ngoại lệ đã khai <span className="font-normal text-muted">({entries.rows.length})</span></span>}>
            <DataTable
              rows={entries.rows}
              loading={entries.loading}
              empty="Chưa khai ngoại lệ nào"
              minWidth="36rem"
              columns={[
                { key: "order", label: "Đơn", mono: true, render: (e) => e.OrderNId },
                { key: "code", label: "Mã", mono: true, render: (e) => `${e.CodeNId} · ${e.Group ?? ""}` },
                { key: "dock", label: "Dock", mono: true, render: (e) => e.Dock ?? "—" },
                { key: "comment", label: "Ghi chú", render: (e) => e.Comment ?? "—" },
              ]}
            />
          </Panel>
        </div>
      </div>
      <Toast message={toast.message} onDone={toast.clear} />
    </>
  );
}
