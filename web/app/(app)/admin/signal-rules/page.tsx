"use client";

import { BellRing, Clock3, Radio, Timer } from "lucide-react";
import { Badge, DataTable, Panel, RefreshButton, ResponsePanel } from "@/components/ui";
import { useEntitySet } from "@/lib/hooks";
import { AdminHeader } from "../AdminTabs";

type Rule = { NId: string; Trigger: string; TimerNId: string | null; EventType: string | null; App: string; Command: string; Enabled: boolean };
type SystemTimer = { NId: string; NextDueOn: string | null; IntervalSeconds: number; Enabled: boolean };

function Enabled({ on }: { on: boolean }) {
  return <Badge tone={on ? "ok" : "neutral"}>{on ? "Bật" : "Tắt"}</Badge>;
}

/** Rule gọi command khi timer tới hạn hoặc khi event được phát; timer tạo lúc chạy có mã định danh. */
export default function SignalRulesPage() {
  const rules = useEntitySet<Rule>("Foundation", "SignalRule", { $orderby: "NId" });
  const timers = useEntitySet<SystemTimer>("Foundation", "SystemTimer", { $orderby: "NextDueOn" });
  return (
    <>
      <AdminHeader description="Rule tắt hoặc trỏ sai command thì không có gì chạy, và màn nghiệp vụ không báo lỗi nào." />
      <div className="flex flex-col gap-5">
        <Panel
          flush
          title={
            <span className="flex items-center gap-2">
              Signal rule <Badge>{rules.rows.length}</Badge>
            </span>
          }
          actions={<RefreshButton onClick={() => void rules.reload()} busy={rules.loading} />}
        >
          {rules.error && (
            <div className="p-4">
              <ResponsePanel result={rules.error} />
            </div>
          )}
          <DataTable
            rows={rules.rows}
            loading={rules.loading}
            empty="Chưa có signal rule"
            emptyIcon={BellRing}
            columns={[
              { key: "nid", label: "NId", render: (r) => <span className="font-medium text-ink">{r.NId}</span>, mono: true },
              {
                key: "trigger",
                label: "Kích hoạt",
                render: (r) =>
                  r.Trigger === "Timer" ? (
                    <Badge tone="accent" mono>
                      <Timer aria-hidden className="size-3" />
                      {r.TimerNId}
                    </Badge>
                  ) : (
                    <Badge tone="primary" mono>
                      <Radio aria-hidden className="size-3" />
                      {r.EventType}
                    </Badge>
                  ),
              },
              { key: "command", label: "Gọi command", render: (r) => `${r.App} / ${r.Command}`, mono: true },
              { key: "enabled", label: "Trạng thái", render: (r) => <Enabled on={r.Enabled} /> },
            ]}
          />
        </Panel>
        <Panel
          flush
          title={
            <span className="flex items-center gap-2">
              Timer <Badge>{timers.rows.length}</Badge>
            </span>
          }
          actions={<RefreshButton onClick={() => void timers.reload()} busy={timers.loading} />}
        >
          <DataTable
            rows={timers.rows}
            loading={timers.loading}
            empty="Chưa có timer"
            emptyIcon={Clock3}
            columns={[
              { key: "nid", label: "NId", render: (t) => <span className="font-medium text-ink">{t.NId}</span>, mono: true },
              { key: "next", label: "Tới hạn kế (UTC)", render: (t) => t.NextDueOn?.slice(0, 16).replace("T", " ") ?? "—", mono: true },
              { key: "interval", label: "Nhịp", render: (t) => (t.IntervalSeconds > 0 ? `${t.IntervalSeconds} s` : "một lần") },
              { key: "enabled", label: "Trạng thái", render: (t) => <Enabled on={t.Enabled} /> },
            ]}
          />
        </Panel>
      </div>
    </>
  );
}
