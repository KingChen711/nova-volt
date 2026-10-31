"use client";

import { useState } from "react";
import { AreaTabs } from "@/components/AreaTabs";
import { Badge, Button, DataTable, Field, PageHeader, Panel, RefreshButton, ResponsePanel, Toast, useToast } from "@/components/ui";
import { useAuth } from "@/lib/auth";
import { useEntitySet } from "@/lib/hooks";
import { postCommand, quote, type MomResult } from "@/lib/mom";

type Trailer = { NId: string; Status: string | null; LocationNId: string | null; Route: string | null };
type Skid = { NId: string; Route: string | null; PoNo: string | null; SupplierCode: string | null; DockCode: string | null; PalletCode: string | null; SkidNo: string | null; MaterialNId: string | null; QuantityPerBox: number; BoxCount: number; Verified: boolean; MtuNId: string | null };

const app = "YardApp";

/** Nhận skid ở dock: quét nhãn, quét vị trí cất; đọc số skid đã nhận và còn lại của xe. */
export default function ReceivePage() {
  const { session } = useAuth();
  const toast = useToast();
  const trailers = useEntitySet<Trailer>(app, "Trailer", { $filter: "ContentType eq 'Component' and (Status eq 'Full' or Status eq 'Tapped')", $orderby: "LocationNId", $top: "100" });
  const [picked, setPicked] = useState("");
  const skids = useEntitySet<Skid>(app, `TrailerSkids(Barcode=${quote(picked || "-")})`, { $orderby: "PoNo,SkidNo" });
  const [label, setLabel] = useState("");
  const [location, setLocation] = useState("");
  const [confirm, setConfirm] = useState(false);
  const [result, setResult] = useState<MomResult | null>(null);
  const [busy, setBusy] = useState(false);
  const received = skids.rows.filter((s) => s.Verified).length;
  const open = skids.rows.length - received;

  async function run(command: string, body: Record<string, unknown>, done: string) {
    if (!session) {
      return false;
    }

    setBusy(true);
    const r = await postCommand(session.accessToken, app, command, body);
    setBusy(false);
    setResult(r);
    if (r.ok) {
      toast.show(done);
      await Promise.all([trailers.reload(), skids.reload()]);
    }

    return r.ok;
  }

  async function receive() {
    if (await run("ReceiveSkid", { Barcode: picked, SkidBarcode: label, StorageLocation: location, ConfirmNotDefault: confirm }, "Đã nhận skid")) {
      setLabel("");
      setConfirm(false);
    }
  }

  return (
    <>
      <PageHeader title="Nhận skid" description="Quét nhãn skid, quét vị trí cất; skid thành MTU ở vị trí đó">
        <RefreshButton onClick={() => void Promise.all([trailers.reload(), skids.reload()])} busy={trailers.loading} />
      </PageHeader>
      <AreaTabs slug="yard" label="Màn cổng và dock" />
      <Toast message={toast.message} onDone={toast.clear} />

      <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_26rem]">
        <div className="flex flex-col gap-4">
          <Panel flush title={<>Xe chờ dỡ <span className="font-normal text-muted">({trailers.rows.length})</span></>}>
            <DataTable
              rows={trailers.rows}
              loading={trailers.loading}
              empty="Không có xe đang chờ dỡ"
              columns={[
                {
                  key: "barcode",
                  label: "Trailer",
                  mono: true,
                  render: (t) => (
                    <button type="button" onClick={() => setPicked(t.NId)} className="cursor-pointer text-primary underline underline-offset-2 hover:text-primary-hover">
                      {t.NId}
                    </button>
                  ),
                },
                { key: "status", label: "Trạng thái", render: (t) => <Badge>{t.Status}</Badge> },
                { key: "location", label: "Vị trí", mono: true, render: (t) => t.LocationNId ?? "—" },
                { key: "route", label: "Route", mono: true, render: (t) => t.Route ?? "—" },
              ]}
            />
          </Panel>

          {picked && (
            <Panel flush title={<>Skid của {picked} <span className="font-normal text-muted">({skids.rows.length})</span></>}>
              {skids.error ? (
                <ResponsePanel result={skids.error} />
              ) : (
                <DataTable
                  rows={skids.rows}
                  loading={skids.loading}
                  empty="Xe không có skid trong lịch đang mở"
                  columns={[
                    { key: "po", label: "Đơn", mono: true, render: (s) => s.PoNo },
                    { key: "skid", label: "Skid", mono: true, render: (s) => s.SkidNo },
                    { key: "route", label: "Route gốc", mono: true, render: (s) => s.Route },
                    { key: "supplier", label: "Nhà cung cấp", mono: true, render: (s) => s.SupplierCode },
                    { key: "dock", label: "Dock", mono: true, render: (s) => s.DockCode },
                    { key: "material", label: "Material", mono: true, render: (s) => s.MaterialNId },
                    { key: "quantity", label: "Số lượng", render: (s) => s.QuantityPerBox * s.BoxCount },
                    { key: "state", label: "Trạng thái", render: (s) => <Badge tone={s.Verified ? "ok" : "neutral"}>{s.Verified ? "Đã nhận" : "Chưa nhận"}</Badge> },
                  ]}
                />
              )}
            </Panel>
          )}
          <ResponsePanel result={result} />
        </div>

        <Panel title={picked ? `Nhận trên ${picked}` : "Chọn một xe"}>
          {!picked ? (
            <p className="text-muted">Bấm mã xe trong danh sách để nhận skid.</p>
          ) : (
            <form
              className="flex flex-col gap-3"
              onSubmit={(e) => {
                e.preventDefault();
                void receive();
              }}
            >
              <p>
                Đã nhận <span className="font-bold">{received}</span>, còn <span className="font-bold">{open}</span> skid
              </p>
              <Field id="rc-label" label="Nhãn skid" required mono value={label} onChange={setLabel} hint={`${label.length}/40 ký tự, giữ nguyên khoảng trắng`} />
              <Field id="rc-location" label="Vị trí cất" required mono value={location} onChange={setLocation} hint="Tên vị trí hoặc mã phụ" />
              <label className="flex items-center gap-2">
                <input type="checkbox" checked={confirm} onChange={(e) => setConfirm(e.target.checked)} className="size-4" />
                Xác nhận cất khác vị trí mặc định
              </label>
              <div className="flex flex-wrap gap-2">
                <Button type="submit" busy={busy} disabled={!label || !location}>
                  Nhận skid
                </Button>
                <Button type="button" variant="secondary" busy={busy} onClick={() => void run("CompleteUnloading", { Barcode: picked, ConfirmMissing: open > 0 && confirm }, "Đã dỡ xong")}>
                  Dỡ xong
                </Button>
              </div>
              {open > 0 && <p className="text-xs text-muted">Còn skid chưa nhận: tick ô xác nhận để dỡ thiếu; MOM gửi email danh sách.</p>}
            </form>
          )}
        </Panel>
      </div>
    </>
  );
}
