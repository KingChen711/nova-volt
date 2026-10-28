"use client";

import { CalendarClock, ClipboardList, Factory, Lock, Snowflake, TimerReset } from "lucide-react";
import { useMemo, useState } from "react";
import { Badge, Button, DataTable, EmptyState, PageHeader, Panel, RefreshButton, ResponsePanel, Toast, useToast } from "@/components/ui";
import { areas, visibleAreas } from "@/lib/areas";
import { useAuth } from "@/lib/auth";
import { useCommand, useEntitySet } from "@/lib/hooks";
import { quote } from "@/lib/mom";

type Equipment = { NId: string; Name: string };
type Schedule = { NId: string; Shift: string; StartTime: string; EndTime: string; TimerNId: string };
type DefaultOrder = { NId: string; ScheduleNId: string; ProcessNId: string };
type Timer = { NId: string; NextDueOn: string | null };
type WorkOrder = {
  Id: string;
  NId: string;
  ProcessNId: string;
  Status: string;
  EstimatedStart: string | null;
  EstimatedEnd: string | null;
};

const app = "Production";

const tone: Record<string, "ok" | "primary" | "warn" | "bad" | "neutral"> = {
  Active: "ok",
  Frozen: "primary",
  New: "warn",
  Edit: "warn",
  Pause: "warn",
  Aborted: "bad",
  Complete: "neutral",
};

function time(value: string | null) {
  return value ? new Date(value).toISOString().replace("T", " ").slice(0, 16) + "Z" : "—";
}

/** OData "a eq x or a eq y"; rỗng thì một điều kiện không khớp gì, để không đọc cả bảng. */
function anyOf(field: string, values: string[]) {
  return values.length > 0 ? values.map((v) => `${field} eq ${quote(v)}`).join(" or ") : `${field} eq ''`;
}

/**
 * Lệnh theo ca của một khu: ca (PS) và timer đầu ca, DWO, WO mà timer đã mở, đóng băng, tự hoàn tất. Đổi trạng thái và
 * hủy đi qua command của app Production, nên pre-check của team chặn thì màn hiện nguyên thân lỗi (lab L06).
 */
export default function LineWorkOrdersPage() {
  const { session } = useAuth();
  const line = areas.find((a) => a.slug === "line")!;
  const allowed = !session || visibleAreas(session.roles).includes(line);
  const lead = !!session && (session.roles.includes("line-lead") || session.roles.includes("mom-admin"));

  const areaList = useEntitySet<Equipment>(app, "Equipment", { $filter: "Level eq 'Area' and startswith(NId,'NV1-B')", $orderby: "NId" });
  const [picked, setPicked] = useState("");
  const area = picked || areaList.rows[0]?.NId || "";

  const schedules = useEntitySet<Schedule>(app, "ProductionSchedule", {
    $filter: area ? `AreaEquipmentNId eq ${quote(area)}` : "NId eq ''",
    $orderby: "StartTime",
  });
  const scheduleNIds = schedules.rows.map((s) => s.NId);
  const defaults = useEntitySet<DefaultOrder>(app, "DefaultWorkOrder", { $filter: anyOf("ScheduleNId", scheduleNIds) });
  const timers = useEntitySet<Timer>("Foundation", "SystemTimer", { $filter: anyOf("NId", schedules.rows.map((s) => s.TimerNId)) });
  const processes = useMemo(() => [...new Set(defaults.rows.map((d) => d.ProcessNId))], [defaults.rows]);
  const orders = useEntitySet<WorkOrder>(app, "WorkOrder", {
    $filter: anyOf("ProcessNId", processes),
    $orderby: "EstimatedStart desc",
    $top: "40",
  });

  const abort = useCommand(app, "AbortWorkOrder");
  const change = useCommand(app, "SetWorkOrderStatus");
  const [last, setLast] = useState<"abort" | "change">("abort");
  const toast = useToast();

  async function act(kind: "abort" | "change", nid: string, status?: string) {
    setLast(kind);
    const result = kind === "abort" ? await abort.send({ NId: nid }) : await change.send({ NId: nid, Status: status });
    if (result?.ok) {
      toast.show(kind === "abort" ? `Đã hủy ${nid}` : `${nid} → ${status}`);
      await orders.reload();
    }
  }

  if (!allowed) {
    return (
      <Panel>
        <EmptyState icon={Lock} title="Role của bạn không được vào khu Xưởng">
          Khu này dành cho role {line.roles.join(", ")}.
        </EmptyState>
      </Panel>
    );
  }

  const counts = orders.rows.reduce<Record<string, number>>((acc, w) => ({ ...acc, [w.Status]: (acc[w.Status] ?? 0) + 1 }), {});

  return (
    <>
      <PageHeader icon={Factory} title="Lệnh theo ca" description="Timer đầu ca đóng băng WO ca trước, mở WO mới cho mỗi DWO, tự hoàn tất WO đóng băng đủ giờ">
        <div className="flex items-end gap-2">
          <label className="flex flex-col gap-1 text-xs font-medium text-muted">
            Khu
            <select
              aria-label="Khu sản xuất"
              value={area}
              onChange={(e) => setPicked(e.target.value)}
              className="min-h-touch rounded-lg border border-line bg-surface px-3 font-mono text-sm text-ink"
            >
              {areaList.rows.map((a) => (
                <option key={a.NId} value={a.NId}>
                  {a.NId}
                </option>
              ))}
            </select>
          </label>
          <RefreshButton
            busy={orders.loading}
            onClick={() => void Promise.all([schedules.reload(), defaults.reload(), timers.reload(), orders.reload()])}
          />
        </div>
      </PageHeader>

      <div className="grid gap-5 xl:grid-cols-[minmax(0,1fr)_22rem]">
        <Panel
          flush
          title={
            <span className="flex flex-wrap items-center gap-2">
              Work order <Badge>{orders.rows.length}</Badge>
              {Object.entries(counts).map(([status, n]) => (
                <Badge key={status} tone={tone[status] ?? "neutral"}>
                  {status} {n}
                </Badge>
              ))}
            </span>
          }
        >
          <DataTable
            rows={orders.rows}
            loading={orders.loading}
            empty="Khu này chưa có WO: timer đầu ca chưa chạy lần nào"
            emptyIcon={ClipboardList}
            minWidth="44rem"
            columns={[
              { key: "nid", label: "WO", mono: true, render: (w) => w.NId },
              { key: "process", label: "Process", mono: true, render: (w) => w.ProcessNId },
              {
                key: "status",
                label: "Trạng thái",
                render: (w) => (
                  <Badge tone={tone[w.Status] ?? "neutral"}>
                    {w.Status === "Frozen" && <Snowflake aria-hidden className="size-3" />}
                    {w.Status}
                  </Badge>
                ),
              },
              { key: "window", label: "Khung ca (UTC)", mono: true, render: (w) => (
                <>
                  <span className="whitespace-nowrap">{time(w.EstimatedStart)}</span>{" "}
                  <span className="whitespace-nowrap">→ {time(w.EstimatedEnd).slice(11)}</span>
                </>
              ) },
              {
                key: "actions",
                label: "",
                className: "whitespace-nowrap",
                render: (w) =>
                  lead && !["Complete", "Aborted"].includes(w.Status) ? (
                    <span className="flex justify-end gap-1">
                      {w.Status === "Active" && (
                        <Button variant="ghost" className="min-h-8 px-2 text-xs" onClick={() => void act("change", w.NId, "Pause")}>
                          Tạm dừng
                        </Button>
                      )}
                      <Button variant="danger" className="min-h-8 px-2 text-xs" onClick={() => void act("abort", w.NId)}>
                        Hủy
                      </Button>
                    </span>
                  ) : null,
              },
            ]}
          />
        </Panel>

        <div className="flex flex-col gap-5">
          <Panel title="Ca và timer đầu ca">
            {schedules.rows.length === 0 ? (
              <p className="text-sm text-muted">Khu chưa có production schedule.</p>
            ) : (
              <ul className="flex flex-col gap-3">
                {schedules.rows.map((s) => {
                  const timer = timers.rows.find((t) => t.NId === s.TimerNId);
                  const dwo = defaults.rows.filter((d) => d.ScheduleNId === s.NId);
                  return (
                    <li key={s.NId} className="rounded-lg border border-line bg-raised p-3">
                      <div className="flex items-center justify-between gap-2">
                        <span className="flex items-center gap-2 text-sm font-semibold">
                          <CalendarClock aria-hidden className="size-4 text-muted" />
                          Ca {s.Shift}
                        </span>
                        <span className="font-mono text-xs">
                          {s.StartTime}–{s.EndTime}
                        </span>
                      </div>
                      <p className="mt-1 flex items-center gap-1.5 text-xs text-muted">
                        <TimerReset aria-hidden className="size-3.5" />
                        Timer chạy lần tới <span className="font-mono text-ink">{time(timer?.NextDueOn ?? null)}</span>
                      </p>
                      <p className="mt-1 text-xs text-muted">
                        DWO: <span className="font-mono text-ink">{dwo.map((d) => d.ProcessNId).join(", ") || "—"}</span>
                      </p>
                    </li>
                  );
                })}
              </ul>
            )}
          </Panel>
          {lead ? (
            <Panel title="Kết quả lệnh gần nhất">
              {(last === "abort" ? abort.result : change.result) ? (
                <ResponsePanel result={last === "abort" ? abort.result : change.result} />
              ) : (
                <p className="text-sm text-muted">Hủy hay tạm dừng một WO: thân response hiện ở đây, kể cả lỗi pre-check.</p>
              )}
            </Panel>
          ) : (
            <p className="text-xs text-muted">Chỉ line-lead đổi trạng thái hay hủy WO.</p>
          )}
        </div>
      </div>
      <Toast message={toast.message} onDone={toast.clear} />
    </>
  );
}
