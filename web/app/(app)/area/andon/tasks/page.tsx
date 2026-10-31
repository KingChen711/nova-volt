"use client";

import { useState } from "react";
import { AreaTabs } from "@/components/AreaTabs";
import { Badge, Button, DataTable, Field, PageHeader, Panel, RefreshButton, ResponsePanel, Toast, useToast } from "@/components/ui";
import { useAuth } from "@/lib/auth";
import { useEntitySet } from "@/lib/hooks";
import { postCommand, quote, type MomResult } from "@/lib/mom";

type Task = { TaskNId: string; RequestNId: string; RouteNId: string; MaterialNId: string; Quantity: number; KanbanNo: string | null; SourceBufferNId: string; DestinationBufferNId: string; Status: string; Priority: number | null; LeadTimeStatus: string; MtuNId: string | null };
type Scanned = { MtuNId: string; MaterialNId: string; Quantity: number; CurrentBufferNId: string; ExpirationDate: string | null; IsInSourceBuffer: boolean; IsFefo: boolean; FefoMtuNId: string | null };

const app = "PullApp";
const legs = [
  { level: "4", label: "Tới tier 4" },
  { level: "3", label: "Tới tier 3" },
  { level: "2", label: "Tới tier 2" },
];

/** Người chở: chọn khu và chặng, nhận việc, quét lô, ghi lô, hoàn tất. Hoàn tất mới dời lô và sinh chặng dưới. */
export default function TasksPage() {
  const { session } = useAuth();
  const toast = useToast();
  const [area, setArea] = useState("MOD-L1");
  const [level, setLevel] = useState("4");
  const [picked, setPicked] = useState<Task | null>(null);
  const [label, setLabel] = useState("");
  const [scanned, setScanned] = useState("");
  const [result, setResult] = useState<MomResult | null>(null);
  const [busy, setBusy] = useState(false);
  const tasks = useEntitySet<Task>(app, `PullTasks(Area=${quote(area)},Level=${quote(level)})`, {});
  const lots = useEntitySet<Scanned>(app, `ScanLot(TaskNId=${quote(picked?.TaskNId ?? "-")},Label=${quote(scanned || "-")})`, {});

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
      await tasks.reload();
    }

    return r.ok;
  }

  return (
    <>
      <PageHeader title="Việc chở" description="Lô do người chở quét; ghi lô chưa dời lô, hoàn tất mới dời và sinh yêu cầu bù chặng dưới">
        <RefreshButton onClick={() => void tasks.reload()} busy={tasks.loading} />
      </PageHeader>
      <AreaTabs slug="andon" label="Màn gọi hàng" />
      <Toast message={toast.message} onDone={toast.clear} />

      <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_26rem]">
        <div className="flex flex-col gap-4">
          <Panel title="Khu và chặng">
            <div className="flex flex-wrap items-end gap-4">
              <div className="w-48">
                <Field id="tk-area" label="Khu của route" mono value={area} onChange={setArea} />
              </div>
              <div className="flex gap-2">
                {legs.map((l) => (
                  <Button key={l.level} variant={level === l.level ? "primary" : "secondary"} onClick={() => setLevel(l.level)}>
                    {l.label}
                  </Button>
                ))}
              </div>
            </div>
          </Panel>
          <Panel flush title={<>Việc đang mở <span className="font-normal text-muted">({tasks.rows.length})</span></>}>
            <DataTable
              rows={tasks.rows}
              loading={tasks.loading}
              empty="Không có việc nào ở chặng này"
              columns={[
                {
                  key: "task",
                  label: "Việc chở",
                  mono: true,
                  render: (t) => (
                    <button
                      type="button"
                      onClick={() => {
                        setPicked(t);
                        setScanned("");
                        setLabel("");
                      }}
                      className="cursor-pointer text-primary underline underline-offset-2 hover:text-primary-hover"
                    >
                      {t.TaskNId}
                    </button>
                  ),
                },
                { key: "material", label: "Material", mono: true, render: (t) => t.MaterialNId },
                { key: "kanban", label: "Kanban", mono: true, render: (t) => t.KanbanNo ?? "—" },
                { key: "source", label: "Lấy từ", mono: true, render: (t) => t.SourceBufferNId },
                { key: "destination", label: "Giao tới", mono: true, render: (t) => t.DestinationBufferNId },
                { key: "priority", label: "Mức", render: (t) => (t.Priority === 1 ? <Badge tone="warn">Gấp</Badge> : "Thường") },
                { key: "status", label: "Trạng thái", render: (t) => <Badge>{t.Status}</Badge> },
                { key: "lead", label: "Hạn chặng", render: (t) => <Badge tone={t.LeadTimeStatus === "Late" ? "bad" : "ok"}>{t.LeadTimeStatus}</Badge> },
              ]}
            />
          </Panel>
          <ResponsePanel result={result} />
        </div>

        <Panel title={picked ? `Việc ${picked.TaskNId}` : "Chọn một việc"}>
          {!picked ? (
            <p className="text-muted">Bấm mã việc trong danh sách.</p>
          ) : (
            <div className="flex flex-col gap-3">
              <p>
                {picked.MaterialNId}: từ <span className="font-mono">{picked.SourceBufferNId}</span> tới <span className="font-mono">{picked.DestinationBufferNId}</span>
              </p>
              <div className="flex flex-wrap gap-2">
                <Button variant="secondary" busy={busy} onClick={() => void run("AcceptPullTask", { TaskNId: picked.TaskNId }, "Đã nhận việc")}>
                  Nhận việc
                </Button>
                <Button variant="secondary" busy={busy} onClick={() => void run("CompletePullTask", { TaskNId: picked.TaskNId }, "Đã hoàn tất chặng")}>
                  Hoàn tất
                </Button>
              </div>
              <form
                className="flex flex-col gap-3"
                onSubmit={(e) => {
                  e.preventDefault();
                  setScanned(label);
                }}
              >
                <Field id="tk-label" label="Nhãn lô" required mono value={label} onChange={setLabel} hint="Nhãn skid 40 ký tự, nhãn đặt thẳng từ 50 ký tự" />
                <div>
                  <Button type="submit" disabled={!label}>
                    Quét
                  </Button>
                </div>
              </form>
              {scanned && lots.rows.length === 0 && !lots.loading && <p className="text-bad">Không có lô nào đang thuộc một buffer cho nhãn này.</p>}
              {lots.rows.map((l) => (
                <div key={l.MtuNId} className="flex flex-col gap-2 rounded-lg border border-line p-3">
                  <p className="font-mono">{l.MtuNId}</p>
                  <p>
                    {l.Quantity}, buffer <span className="font-mono">{l.CurrentBufferNId}</span>, hạn {l.ExpirationDate ?? "không có"}
                  </p>
                  <div className="flex flex-wrap gap-2">
                    <Badge tone={l.IsInSourceBuffer ? "ok" : "bad"}>{l.IsInSourceBuffer ? "Đúng nguồn" : "Sai nguồn"}</Badge>
                    <Badge tone={l.IsFefo ? "ok" : "warn"}>{l.IsFefo ? "Đúng FEFO" : `Lấy trước: ${l.FefoMtuNId ?? "—"}`}</Badge>
                  </div>
                  <div>
                    <Button busy={busy} onClick={() => void run("RecordLot", { TaskNId: picked.TaskNId, MtuNId: l.MtuNId }, "Đã ghi lô")}>
                      Ghi lô
                    </Button>
                  </div>
                </div>
              ))}
            </div>
          )}
        </Panel>
      </div>
    </>
  );
}
