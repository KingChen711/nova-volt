"use client";

import { useState } from "react";
import { AreaTabs } from "@/components/AreaTabs";
import { Button, DataTable, Field, PageHeader, Panel, ResponsePanel, Toast, useToast } from "@/components/ui";
import { useAuth } from "@/lib/auth";
import { useEntitySet } from "@/lib/hooks";
import { postCommand, quote, type MomResult } from "@/lib/mom";

type Skid = { NId: string; Route: string | null; PoNo: string | null; SkidNo: string | null; SupplierCode: string | null; Verified: boolean };
type Reason = { NId: string };

const app = "YardApp";

/** Chuyển skid chưa nhận giữa hai xe đang ở dock: quét từng nhãn để kiểm, Lưu mới ghi. */
export default function TransferPage() {
  const { session } = useAuth();
  const toast = useToast();
  const reasons = useEntitySet<Reason>(app, "TransferReason", { $orderby: "NId" });
  const [source, setSource] = useState("");
  const [destination, setDestination] = useState("");
  const [reason, setReason] = useState("");
  const [comment, setComment] = useState("");
  const [label, setLabel] = useState("");
  const [scanned, setScanned] = useState<string[]>([]);
  const onSource = useEntitySet<Skid>(app, `TrailerSkids(Barcode=${quote(source || "-")})`, { $filter: "Verified eq false", $orderby: "PoNo,SkidNo" });
  const onDestination = useEntitySet<Skid>(app, `TrailerSkids(Barcode=${quote(destination || "-")})`, { $filter: "Verified eq false", $orderby: "PoNo,SkidNo" });
  const [result, setResult] = useState<MomResult | null>(null);
  const [busy, setBusy] = useState(false);

  async function check() {
    if (!session) {
      return;
    }

    if (scanned.includes(label)) {
      setLabel("");
      return;
    }

    setBusy(true);
    const r = await postCommand(session.accessToken, app, "CheckTransferSkid", { SourceBarcode: source, SkidBarcode: label });
    setBusy(false);
    setResult(r);
    if (r.ok) {
      setScanned([...scanned, label]);
      setLabel("");
    }
  }

  async function save() {
    if (!session) {
      return;
    }

    setBusy(true);
    const r = await postCommand(session.accessToken, app, "TransferSkids", { SourceBarcode: source, DestinationBarcode: destination, ReasonNId: reason, Comments: comment, SkidBarcodes: scanned });
    setBusy(false);
    setResult(r);
    if (r.ok) {
      toast.show("Đã chuyển skid");
      setScanned([]);
      await Promise.all([onSource.reload(), onDestination.reload()]);
    }
  }

  const columns = [
    { key: "po", label: "Đơn", mono: true, render: (s: Skid) => s.PoNo },
    { key: "skid", label: "Skid", mono: true, render: (s: Skid) => s.SkidNo },
    { key: "route", label: "Route gốc", mono: true, render: (s: Skid) => s.Route },
  ];

  return (
    <>
      <PageHeader title="Chuyển skid" description="Chuyển skid chưa nhận giữa hai xe đang đứng ở cửa dock; nhãn skid không đổi" />
      <AreaTabs slug="yard" label="Màn cổng và dock" />
      <Toast message={toast.message} onDone={toast.clear} />

      <div className="grid gap-4 xl:grid-cols-[26rem_minmax(0,1fr)]">
        <Panel title="Thông tin chuyển">
          <form
            className="flex flex-col gap-3"
            onSubmit={(e) => {
              e.preventDefault();
              void check();
            }}
          >
            <div className="flex flex-col gap-1">
              <label htmlFor="tf-reason" className="font-bold">Lý do<span className="ml-1 text-bad" aria-hidden>*</span></label>
              <select id="tf-reason" value={reason} onChange={(e) => setReason(e.target.value)} className="min-h-touch rounded-lg border border-field bg-surface px-3">
                <option value="">Chọn lý do</option>
                {reasons.rows.map((r) => (
                  <option key={r.NId} value={r.NId}>{r.NId}</option>
                ))}
              </select>
            </div>
            <Field id="tf-comment" label="Ghi chú" required value={comment} onChange={setComment} />
            <Field id="tf-source" label="Xe nguồn" required mono value={source} onChange={setSource} />
            <Field id="tf-destination" label="Xe đích" required mono value={destination} onChange={setDestination} />
            <Field id="tf-label" label="Nhãn skid" mono value={label} onChange={setLabel} hint={`${label.length}/40 ký tự; Enter để kiểm`} />
            <div className="flex flex-wrap gap-2">
              <Button type="submit" variant="secondary" busy={busy} disabled={!label || !source}>
                Kiểm nhãn
              </Button>
              <Button type="button" busy={busy} disabled={scanned.length === 0 || !reason || !comment || !destination} onClick={() => void save()}>
                Lưu ({scanned.length} skid)
              </Button>
            </div>
          </form>
        </Panel>

        <div className="flex flex-col gap-4">
          <Panel flush title={<>Skid chưa nhận trên xe nguồn <span className="font-normal text-muted">({onSource.rows.length})</span></>}>
            <DataTable rows={onSource.rows} loading={onSource.loading} empty="Xe nguồn không có skid chưa nhận" columns={columns} />
          </Panel>
          <Panel flush title={<>Skid chưa nhận trên xe đích <span className="font-normal text-muted">({onDestination.rows.length})</span></>}>
            <DataTable rows={onDestination.rows} loading={onDestination.loading} empty="Xe đích không có skid chưa nhận" columns={columns} />
          </Panel>
          <ResponsePanel result={result} />
        </div>
      </div>
    </>
  );
}
