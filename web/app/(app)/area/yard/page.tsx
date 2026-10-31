"use client";

import { useState } from "react";
import { AreaTabs } from "@/components/AreaTabs";
import { Badge, Button, DataTable, EmptyState, Field, PageHeader, Panel, RefreshButton, ResponsePanel, Toast, useToast } from "@/components/ui";
import { areas, visibleAreas } from "@/lib/areas";
import { useAuth } from "@/lib/auth";
import { useEntitySet } from "@/lib/hooks";
import { postCommand, quote, type MomResult } from "@/lib/mom";

type Trailer = { NId: string; TrailerNo: string | null; Status: string | null; ContentType: string | null; LocationNId: string | null };
type Movement = { NId: string; TrailerNId: string; FromNId: string | null; ToNId: string; Status: string; Priority: string | null };
type Schedule = { NId: string; TrailerNId: string; DockNId: string | null; Status: string | null; Comments: string | null; CreatedOn: string };
type Pallet = { NId: string; MaterialNId: string | null; Quantity: number; Status: string | null };

const app = "YardApp";
const next: Record<string, string> = { Created: "Accepted", Accepted: "Picked", Picked: "Dropped" };

/**
 * Trailer kho: trailer đỗ trong nhà máy làm kho tạm cho pallet thành phẩm. Đặt loại kho khi trailer rỗng; gọi vào cửa dock rồi
 * người kéo xe đi từng bước của yêu cầu; nạp pallet theo luật (Full, cùng part với pallet đầu, không trộn đã đo và chưa đo, không
 * hàng bị giữ); Complete Load; dỡ cả trailer hoặc một pallet; ra yard rồi mới gỡ loại kho.
 */
export default function StorageTrailerPage() {
  const { session } = useAuth();
  const area = areas.find((a) => a.slug === "yard")!;
  const allowed = !session || visibleAreas(session.roles).includes(area);
  const toast = useToast();

  const trailers = useEntitySet<Trailer>(app, "Trailer", { $orderby: "NId", $top: "100" });
  const movements = useEntitySet<Movement>(app, "TrailerMovement", { $filter: "Status ne 'Dropped'", $orderby: "CreatedOn" });
  const [picked, setPicked] = useState("");
  const schedules = useEntitySet<Schedule>(app, "TrailerSchedule", { $filter: `TrailerNId eq ${quote(picked || "-")}`, $orderby: "CreatedOn desc", $top: "1" });
  const pallets = useEntitySet<Pallet>(app, "Mtua", { $filter: `ParentMtuaNId eq ${quote(picked || "-")}`, $orderby: "NId" });
  const [form, setForm] = useState({ barcode: "", yard: "NV1-CMN-YARD-Y01", type: "FGStorage", dock: "NV1-CMN-DOCK-D01", destination: "", pallet: "", location: "", comments: "" });
  const [result, setResult] = useState<MomResult | null>(null);
  const [busy, setBusy] = useState(false);

  async function run(command: string, body: Record<string, unknown>, done: string) {
    if (!session) {
      return;
    }

    setBusy(true);
    const r = await postCommand(session.accessToken, app, command, body);
    setBusy(false);
    setResult(r);
    if (r.ok) {
      toast.show(done);
      await Promise.all([trailers.reload(), movements.reload(), schedules.reload(), pallets.reload()]);
    }
  }

  if (!allowed) {
    return (
      <Panel>
        <EmptyState title="Role của bạn không được vào khu Cổng và dock">
          Khu này dành cho role {area.roles.join(", ")}.
        </EmptyState>
      </Panel>
    );
  }

  const trailer = trailers.rows.find((t) => t.NId === picked);
  const field = (id: keyof typeof form, label: string) => (
    <Field id={`tr-${id}`} label={label} mono value={form[id]} onChange={(v) => setForm({ ...form, [id]: v })} />
  );
  const schedule = schedules.rows[0];

  return (
    <>
      <PageHeader title="Trailer kho" description="Trailer làm kho tạm cho pallet thành phẩm: loại kho, gọi vào dock, nạp, chốt, dỡ">
        <RefreshButton busy={trailers.loading} onClick={() => void Promise.all([trailers.reload(), movements.reload()])} />
      </PageHeader>
      <AreaTabs slug="yard" label="Màn của khu Cổng và dock" />

      <div className="grid gap-5 xl:grid-cols-[minmax(0,1fr)_26rem]">
        <div className="flex flex-col gap-5">
          <Panel flush title={<span className="flex items-center gap-2">Trailer <span className="font-normal text-muted">({trailers.rows.length})</span></span>}>
            {trailers.error ? (
              <div className="p-4">
                <ResponsePanel result={trailers.error} />
              </div>
            ) : (
              <DataTable
                rows={trailers.rows}
                loading={trailers.loading}
                empty="Chưa có trailer nào"
                minWidth="40rem"
                columns={[
                  {
                    key: "nid",
                    label: "Barcode",
                    mono: true,
                    render: (t) => (
                      <button type="button" className="cursor-pointer text-primary underline underline-offset-2 hover:text-primary-hover" onClick={() => setPicked(t.NId)}>
                        {t.NId}
                      </button>
                    ),
                  },
                  { key: "status", label: "Trạng thái", render: (t) => <Badge tone={t.Status === "Full" ? "ok" : t.Status === "Tapped" ? "accent" : "neutral"}>{t.Status ?? "—"}</Badge> },
                  { key: "type", label: "Loại kho", mono: true, render: (t) => t.ContentType ?? "—" },
                  { key: "loc", label: "Vị trí", mono: true, render: (t) => t.LocationNId ?? "—" },
                ]}
              />
            )}
          </Panel>

          <Panel flush title={<span className="flex items-center gap-2">Yêu cầu kéo xe đang mở <span className="font-normal text-muted">({movements.rows.length})</span></span>}>
            <DataTable
              rows={movements.rows}
              loading={movements.loading}
              empty="Không có yêu cầu kéo xe nào đang mở"
              minWidth="40rem"
              columns={[
                { key: "trailer", label: "Trailer", mono: true, render: (m) => m.TrailerNId },
                { key: "route", label: "Từ → tới", mono: true, render: (m) => `${m.FromNId ?? "—"} → ${m.ToNId}` },
                { key: "status", label: "Bước", render: (m) => <Badge>{m.Status}</Badge> },
                {
                  key: "next",
                  label: "",
                  render: (m) => (
                    <Button variant="secondary" busy={busy} onClick={() => void run("UpdateTrailerMovement", { MovementNId: m.NId, Status: next[m.Status] }, `${m.TrailerNId}: ${next[m.Status]}`)}>
                      {next[m.Status]}
                    </Button>
                  ),
                },
              ]}
            />
          </Panel>
        </div>

        <div className="flex flex-col gap-5">
          <Panel title="Trailer mới">
            <div className="grid grid-cols-2 gap-3">
              {field("barcode", "Barcode")}
              {field("yard", "Ô yard")}
            </div>
            <div className="mt-3 flex gap-2">
              <Button busy={busy} disabled={!form.barcode} onClick={() => void run("CreateTrailer", { Barcode: form.barcode }, `Đã khai ${form.barcode}`)}>
                Khai báo
              </Button>
              <Button variant="secondary" busy={busy} disabled={!form.barcode} onClick={() => void run("ArriveEmptyTrailer", { Barcode: form.barcode, LocationNId: form.yard }, `${form.barcode} đã đỗ`)}>
                Đỗ xe rỗng
              </Button>
            </div>
          </Panel>

          <Panel title={trailer ? `Trailer ${trailer.NId}` : "Chọn một trailer"}>
            {trailer ? (
              <div className="flex flex-col gap-4">
                <p className="text-sm text-muted">
                  {trailer.Status} · {trailer.ContentType ?? "không loại kho"} · {trailer.LocationNId ?? "—"}
                  {schedule && (
                    <>
                      {" "}
                      · lịch <span className="font-mono text-xs text-ink">{schedule.Status}</span>
                    </>
                  )}
                </p>
                <div className="flex flex-wrap items-end gap-2">
                  <label className="flex flex-col gap-1 text-sm font-medium">
                    Loại kho
                    <select value={form.type} onChange={(e) => setForm({ ...form, type: e.target.value })} className="min-h-touch rounded-lg border border-field bg-surface px-3 font-mono text-sm">
                      <option>FGStorage</option>
                      <option>GenericStorage</option>
                    </select>
                  </label>
                  <Button variant="secondary" busy={busy} onClick={() => void run("SetTrailerAsStorage", { Barcode: trailer.NId, ContentType: form.type }, "Đã đặt loại kho")}>
                    Đặt loại kho
                  </Button>
                  <Button variant="danger" busy={busy} onClick={() => void run("UnsetTrailerAsStorage", { Barcode: trailer.NId }, "Đã gỡ loại kho")}>
                    Gỡ loại kho
                  </Button>
                </div>
                <div className="grid grid-cols-[1fr_auto] items-end gap-2">
                  {field("dock", "Cửa dock")}
                  <Button variant="secondary" busy={busy} onClick={() => void run("CallStorageTrailer", { Barcode: trailer.NId, DockNId: form.dock }, "Đã gọi vào dock")}>
                    Gọi vào dock
                  </Button>
                  {field("destination", "Chuyển tới")}
                  <Button variant="secondary" busy={busy} disabled={!form.destination} onClick={() => void run("CreateTrailerMovementRequest", { Barcode: trailer.NId, DestinationNId: form.destination }, "Đã tạo yêu cầu kéo")}>
                    Tạo yêu cầu kéo
                  </Button>
                </div>
                <div className="grid grid-cols-[1fr_auto] items-end gap-2">
                  {field("pallet", "Quét pallet")}
                  <Button busy={busy} disabled={!form.pallet} onClick={() => void run("LoadPalletIntoStorageTrailer", { Barcode: trailer.NId, PalletNId: form.pallet }, `Đã nạp ${form.pallet}`)}>
                    Nạp
                  </Button>
                  {field("location", "Dỡ pallet tới")}
                  <Button variant="secondary" busy={busy} disabled={!form.pallet || !form.location} onClick={() => void run("UnloadPalletFromStorageTrailer", { Barcode: trailer.NId, PalletNId: form.pallet, LocationNId: form.location }, `Đã dỡ ${form.pallet}`)}>
                    Dỡ một pallet
                  </Button>
                </div>
                {field("comments", "Ghi chú (kho chung)")}
                <div className="flex flex-wrap gap-2">
                  <Button busy={busy} onClick={() => void run("CompleteStorageTrailerLoad", { Barcode: trailer.NId, Comments: form.comments || undefined }, "Đã chốt đầy")}>
                    Complete Load
                  </Button>
                  <Button variant="danger" busy={busy} onClick={() => void run("UnloadStorageTrailer", { Barcode: trailer.NId, Comments: form.comments || undefined }, "Đã dỡ trailer")}>
                    Dỡ cả trailer
                  </Button>
                </div>
                <div>
                  <p className="mb-1 text-sm font-medium">Pallet trong trailer ({pallets.rows.length})</p>
                  <ul className="flex flex-col gap-1 font-mono text-xs">
                    {pallets.rows.map((p) => (
                      <li key={p.NId}>
                        {p.NId} · {p.MaterialNId} · {p.Status}
                      </li>
                    ))}
                  </ul>
                </div>
              </div>
            ) : (
              <p className="text-sm text-muted">Bấm barcode trong danh sách để thao tác.</p>
            )}
          </Panel>

          {result && <ResponsePanel result={result} />}
        </div>
      </div>
      <Toast message={toast.message} onDone={toast.clear} />
    </>
  );
}
