"use client";

import { useState } from "react";
import { AreaTabs } from "@/components/AreaTabs";
import { Button, DataTable, Field, PageHeader, Panel, RefreshButton, ResponsePanel, Toast, useToast } from "@/components/ui";
import { useAuth } from "@/lib/auth";
import { useEntitySet } from "@/lib/hooks";
import { postCommand, type MomResult } from "@/lib/mom";

type Line = { NId: string; PoNo: string; PoLine: string; MaterialNId: string; Batch: string; Quantity: number; ExpirationDate: string | null };

const app = "PullApp";

/** Nhận nguyên liệu đặt thẳng: quét nhãn, chọn vị trí, pallet và số lượng theo đơn vị nhận. */
export default function DirectReceivePage() {
  const { session } = useAuth();
  const toast = useToast();
  const lines = useEntitySet<Line>(app, "DirectDeliveryLine", { $orderby: "PoNo desc", $top: "50" });
  const [label, setLabel] = useState("");
  const [location, setLocation] = useState("NV1-CMN-RCV-R02");
  const [pallet, setPallet] = useState("1");
  const [quantity, setQuantity] = useState("1");
  const [result, setResult] = useState<MomResult | null>(null);
  const [busy, setBusy] = useState(false);

  async function receive() {
    if (!session) {
      return;
    }

    setBusy(true);
    const r = await postCommand(session.accessToken, app, "ReceiveDirectMaterial", { Label: label, StorageLocation: location, PalletNumber: pallet, Quantity: Number(quantity) });
    setBusy(false);
    setResult(r);
    if (r.ok) {
      toast.show("Đã nhận lô");
    }
  }

  return (
    <>
      <PageHeader title="Nhận đặt thẳng" description="Lô có tên batch_pallet; thiếu đơn từ ERP vẫn nhận được nhưng lô không có hạn dùng">
        <RefreshButton onClick={() => void lines.reload()} busy={lines.loading} />
      </PageHeader>
      <AreaTabs slug="andon" label="Màn gọi hàng" />
      <Toast message={toast.message} onDone={toast.clear} />

      <div className="grid gap-4 xl:grid-cols-[26rem_minmax(0,1fr)]">
        <Panel title="Nhận lô">
          <form
            className="flex flex-col gap-3"
            onSubmit={(e) => {
              e.preventDefault();
              void receive();
            }}
          >
            <Field id="dr-label" label="Nhãn" required mono value={label} onChange={setLabel} hint={`${label.length} ký tự, tối thiểu 50`} />
            <Field id="dr-location" label="Vị trí cất" required mono value={location} onChange={setLocation} />
            <Field id="dr-pallet" label="Số pallet" required mono value={pallet} onChange={setPallet} />
            <Field id="dr-quantity" label="Số lượng" required mono value={quantity} onChange={setQuantity} hint="Theo đơn vị nhận; MOM quy đổi theo hệ Receiving" />
            <div>
              <Button type="submit" busy={busy} disabled={!label || !location || !pallet}>
                Nhận
              </Button>
            </div>
          </form>
        </Panel>

        <div className="flex flex-col gap-4">
          <Panel flush title={<>Đơn từ ERP <span className="font-normal text-muted">({lines.rows.length})</span></>}>
            <DataTable
              rows={lines.rows}
              loading={lines.loading}
              empty="Chưa có đơn nào"
              columns={[
                { key: "po", label: "Đơn", mono: true, render: (l) => `${l.PoNo}/${l.PoLine}` },
                { key: "material", label: "Material", mono: true, render: (l) => l.MaterialNId },
                { key: "batch", label: "Batch", mono: true, render: (l) => l.Batch },
                { key: "quantity", label: "Số lượng", render: (l) => l.Quantity },
                { key: "expiry", label: "Hạn dùng", mono: true, render: (l) => l.ExpirationDate ?? "—" },
              ]}
            />
          </Panel>
          <ResponsePanel result={result} />
        </div>
      </div>
    </>
  );
}
