"use client";

import { useState } from "react";
import { AreaTabs } from "@/components/AreaTabs";
import { Badge, Button, DataTable, Field, PageHeader, Panel, RefreshButton, ResponsePanel, Toast, useToast } from "@/components/ui";
import { useAuth } from "@/lib/auth";
import { useEntitySet } from "@/lib/hooks";
import { postCommand, quote, type MomResult } from "@/lib/mom";

type Tile = { MaterialNId: string; KanbanNo: string | null; RouteNId: string; PartAddressNId: string | null; BufferNId: string; Quantity: number };
type Request = { TaskNId: string; RequestNId: string; MaterialNId: string; DestinationBufferNId: string; SourceBufferNId: string; Status: string; Priority: number | null; LeadTimeStatus: string; RouteNId: string };

const app = "PullApp";

/** Khu gọi hàng: chọn khu, bấm ô của part; một lần bấm sinh cả yêu cầu lẫn việc chở. */
export default function PullPage() {
  const { session } = useAuth();
  const toast = useToast();
  const [area, setArea] = useState("NV1-B1-L1-MOD");
  const [hot, setHot] = useState(false);
  const [result, setResult] = useState<MomResult | null>(null);
  const [busy, setBusy] = useState(false);
  const tiles = useEntitySet<Tile>(app, `LinkedMaterials(EquipmentNId=${quote(area)})`, {});
  const requests = useEntitySet<Request>(app, `AreaRequests(EquipmentNId=${quote(area)})`, {});

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
      await requests.reload();
    }
  }

  return (
    <>
      <PageHeader title="Gọi hàng" description="Part được gọi là part có trong whitelist ở ô giao của khu, kanban và part address còn hiệu lực, hoặc material đặt thẳng khai trên route">
        <RefreshButton onClick={() => void Promise.all([tiles.reload(), requests.reload()])} busy={requests.loading} />
      </PageHeader>
      <AreaTabs slug="line" label="Màn xưởng" />
      <Toast message={toast.message} onDone={toast.clear} />

      <div className="flex flex-col gap-4">
        <Panel title="Khu và part">
          <div className="mb-3 flex flex-wrap items-end gap-4">
            <div className="w-72">
              <Field id="pl-area" label="Khu sản xuất" mono value={area} onChange={setArea} />
            </div>
            <label className="flex min-h-8 items-center gap-2">
              <input type="checkbox" checked={hot} onChange={(e) => setHot(e.target.checked)} className="size-4" />
              Gọi gấp
            </label>
          </div>
          {tiles.rows.length === 0 ? (
            <p className="text-muted">{tiles.loading ? "Đang đọc" : "Khu này không có part gọi được"}</p>
          ) : (
            <div className="flex flex-wrap gap-2">
              {tiles.rows.map((t) => (
                <Button
                  key={t.RouteNId + t.MaterialNId}
                  variant="secondary"
                  busy={busy}
                  onClick={() => void run("CallMaterial", { RouteNId: t.RouteNId, MaterialNId: t.MaterialNId, Quantity: t.Quantity, PartAddressNId: t.PartAddressNId, Hot: hot }, `Đã gọi ${t.MaterialNId}`)}
                >
                  {t.MaterialNId} · {t.KanbanNo ?? "đặt thẳng"} · {t.Quantity}
                </Button>
              ))}
            </div>
          )}
        </Panel>

        <Panel flush title={<>Lệnh đang mở <span className="font-normal text-muted">({requests.rows.length})</span></>}>
          <DataTable
            rows={requests.rows}
            loading={requests.loading}
            empty="Không có lệnh gọi nào đang mở"
            columns={[
              { key: "request", label: "Yêu cầu", mono: true, render: (r) => r.RequestNId },
              { key: "task", label: "Việc chở", mono: true, render: (r) => r.TaskNId },
              { key: "material", label: "Material", mono: true, render: (r) => r.MaterialNId },
              { key: "destination", label: "Ô giao", mono: true, render: (r) => r.DestinationBufferNId },
              { key: "route", label: "Route", mono: true, render: (r) => r.RouteNId },
              { key: "priority", label: "Mức", render: (r) => (r.Priority === 1 ? <Badge tone="warn">Gấp</Badge> : "Thường") },
              { key: "status", label: "Trạng thái", render: (r) => <Badge>{r.Status}</Badge> },
              { key: "lead", label: "Hạn chặng", render: (r) => <Badge tone={r.LeadTimeStatus === "Late" ? "bad" : "ok"}>{r.LeadTimeStatus}</Badge> },
              {
                key: "cancel",
                label: "",
                render: (r) => (
                  <Button variant="secondary" busy={busy} onClick={() => void run("CancelPullTask", { TaskNId: r.TaskNId }, "Đã hủy lệnh")}>
                    Hủy
                  </Button>
                ),
              },
            ]}
          />
        </Panel>
        <ResponsePanel result={result} />
      </div>
    </>
  );
}
