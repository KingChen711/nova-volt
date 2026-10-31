"use client";

import { useState } from "react";
import { AreaTabs } from "@/components/AreaTabs";
import { Button, DataTable, Field, PageHeader, Panel, RefreshButton, ResponsePanel, Toast, useToast } from "@/components/ui";
import { useAuth } from "@/lib/auth";
import { useEntitySet } from "@/lib/hooks";
import { postCommand, quote, type MomResult } from "@/lib/mom";

type Lot = { NId: string; MaterialNId: string; Quantity: number; UnitOfMeasure: string | null; EquipmentNId: string | null };

const app = "PullApp";

/** Nạp lô vào buffer của khu đang chọn; một lô một buffer, nên lô còn ở ô giao phải gỡ trước. */
export default function LoadPage() {
  const { session } = useAuth();
  const toast = useToast();
  const [area, setArea] = useState("NV1-B1-L1-MOD-ASSY");
  const [label, setLabel] = useState("");
  const [scanned, setScanned] = useState("");
  const [result, setResult] = useState<MomResult | null>(null);
  const [busy, setBusy] = useState(false);
  const loaded = useEntitySet<Lot>(app, `LoadedLots(EquipmentNId=${quote(area)})`, { $orderby: "NId" });
  const found = useEntitySet<Lot>(app, `SearchLots(Label=${quote(scanned || "-")})`, { $top: "20" });

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
      await Promise.all([loaded.reload(), found.reload()]);
    }
  }

  return (
    <>
      <PageHeader title="Nạp lô" description="Khai báo lô đã vào chỗ chứa của khu: lô thuộc buffer của khu và tới vị trí của buffer đó">
        <RefreshButton onClick={() => void Promise.all([loaded.reload(), found.reload()])} busy={loaded.loading} />
      </PageHeader>
      <AreaTabs slug="line" label="Màn xưởng" />
      <Toast message={toast.message} onDone={toast.clear} />

      <div className="grid gap-4 xl:grid-cols-[26rem_minmax(0,1fr)]">
        <Panel title="Quét lô">
          <form
            className="flex flex-col gap-3"
            onSubmit={(e) => {
              e.preventDefault();
              setScanned(label);
            }}
          >
            <Field id="ld-area" label="Khu đang chọn" required mono value={area} onChange={setArea} hint="Khu cấp Area không có buffer" />
            <Field id="ld-label" label="Nhãn hoặc mã lô" required mono value={label} onChange={setLabel} />
            <div>
              <Button type="submit" disabled={!label}>
                Tìm lô
              </Button>
            </div>
          </form>
        </Panel>

        <div className="flex flex-col gap-4">
          {scanned && (
            <Panel flush title={<>Lô tìm thấy <span className="font-normal text-muted">({found.rows.length})</span></>}>
              <DataTable
                rows={found.rows}
                loading={found.loading}
                empty="Không tìm thấy lô cho nhãn này"
                columns={[
                  { key: "lot", label: "Lô", mono: true, render: (l) => l.NId },
                  { key: "material", label: "Material", mono: true, render: (l) => l.MaterialNId },
                  { key: "quantity", label: "Số lượng", render: (l) => `${l.Quantity} ${l.UnitOfMeasure ?? ""}` },
                  { key: "where", label: "Vị trí", mono: true, render: (l) => l.EquipmentNId ?? "—" },
                  {
                    key: "actions",
                    label: "",
                    render: (l) => (
                      <div className="flex gap-2">
                        <Button busy={busy} onClick={() => void run("LoadLot", { EquipmentNId: area, MtuNId: l.NId }, `Đã nạp ${l.NId}`)}>
                          Nạp
                        </Button>
                        <Button variant="secondary" busy={busy} onClick={() => void run("UnloadLot", { MtuNId: l.NId }, `Đã gỡ ${l.NId} khỏi buffer`)}>
                          Gỡ khỏi buffer
                        </Button>
                      </div>
                    ),
                  },
                ]}
              />
            </Panel>
          )}
          <Panel flush title={<>Lô trong khu <span className="font-normal text-muted">({loaded.rows.length})</span></>}>
            <DataTable
              rows={loaded.rows}
              loading={loaded.loading}
              empty="Chưa có lô nào trong buffer của khu"
              columns={[
                { key: "lot", label: "Lô", mono: true, render: (l) => l.NId },
                { key: "material", label: "Material", mono: true, render: (l) => l.MaterialNId },
                { key: "quantity", label: "Số lượng", render: (l) => `${l.Quantity} ${l.UnitOfMeasure ?? ""}` },
              ]}
            />
          </Panel>
          <ResponsePanel result={result} />
        </div>
      </div>
    </>
  );
}
