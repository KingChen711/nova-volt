"use client";

import { ArrowRight, BellRing, Clock3, GitBranch, KeyRound, LayoutDashboard, Radio, Timer } from "lucide-react";
import Link from "next/link";
import { useEffect, useState } from "react";
import { Badge, DataTable, PageHeader, Panel, RefreshButton, StatCard } from "@/components/ui";
import { visibleAreas } from "@/lib/areas";
import { useAuth } from "@/lib/auth";
import { useEntitySet } from "@/lib/hooks";
import { readSet } from "@/lib/mom";

type Timer = { NId: string; NextDueOn: string | null; IntervalSeconds: number; Enabled: boolean };

/** Đếm bản ghi bằng `$count=true&$top=0`: chỉ lấy con số, không kéo dòng nào về. */
function useCount(resource: string, filter?: string) {
  const { session } = useAuth();
  const [count, setCount] = useState<number | null>(null);
  useEffect(() => {
    if (!session) {
      return;
    }

    void readSet(session.accessToken, "Foundation", resource, { $count: "true", $top: "0", $filter: filter }).then((r) =>
      setCount(r.ok && r.body && typeof r.body !== "string" ? (r.body["@odata.count"] ?? 0) : null),
    );
  }, [session, resource, filter]);
  return count;
}

function relative(iso: string | null): string {
  if (!iso) {
    return "—";
  }

  const minutes = Math.round((new Date(iso).getTime() - Date.now()) / 60_000);
  if (Math.abs(minutes) < 1) {
    return "bây giờ";
  }

  const text = Math.abs(minutes) < 120 ? `${Math.abs(minutes)} phút` : `${Math.round(Math.abs(minutes) / 60)} giờ`;
  return minutes > 0 ? `sau ${text}` : `trễ ${text}`;
}

function every(seconds: number): string {
  if (seconds <= 0) {
    return "một lần";
  }

  return seconds % 3600 === 0 ? `${seconds / 3600} giờ` : seconds % 60 === 0 ? `${seconds / 60} phút` : `${seconds} giây`;
}

export default function DashboardPage() {
  const { session } = useAuth();
  const keys = useCount("ConfigurationKey");
  const rules = useCount("SignalRule", "Enabled eq true");
  const timers = useCount("SystemTimer", "Enabled eq true");
  const transitions = useCount("StatusTransition");
  const subscriptions = useCount("EventSubscription", "Enabled eq true");
  const upcoming = useEntitySet<Timer>("Foundation", "SystemTimer", { $filter: "Enabled eq true", $orderby: "NextDueOn", $top: "6" });
  const areas = session ? visibleAreas(session.roles).filter((a) => a.slug !== "dashboard") : [];
  const hello = session?.user ?? "";

  return (
    <>
      <PageHeader
        icon={LayoutDashboard}
        title={`Xin chào, ${hello}`}
        description="Tình trạng cấu hình nền của plant và lối vào các khu bạn được làm việc."
      />

      <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-5">
        <StatCard label="Configuration key" value={keys ?? "—"} loading={keys === null} icon={KeyRound} hint="Giá trị cấu hình dạng chuỗi" />
        <StatCard label="Signal rule đang bật" value={rules ?? "—"} loading={rules === null} icon={BellRing} tone="accent" hint="Gọi command theo timer hoặc event" />
        <StatCard label="Timer đang chạy" value={timers ?? "—"} loading={timers === null} icon={Timer} tone="ok" hint="Tạo lúc chạy, có mã định danh" />
        <StatCard label="Transition" value={transitions ?? "—"} loading={transitions === null} icon={GitBranch} hint="State machine dạng dữ liệu" />
        <StatCard label="Subscription theo plant" value={subscriptions ?? "—"} loading={subscriptions === null} icon={Radio} tone="warn" hint="Không tính loại khai sẵn trong model" />
      </div>

      <div className="mt-6 grid gap-6 xl:grid-cols-[1fr_26rem]">
        <section>
          <h2 className="mb-3 text-sm font-semibold tracking-wide text-muted uppercase">Khu của bạn</h2>
          <div className="grid gap-3 sm:grid-cols-2 2xl:grid-cols-3">
            {areas.map((area) => {
              const Icon = area.icon;
              const ready = area.milestone === "R2";
              return (
                <Link
                  key={area.slug}
                  href={area.href}
                  className="group flex flex-col rounded-xl border border-line bg-surface p-4 shadow-[var(--shadow-card)] transition duration-150 hover:-translate-y-0.5 hover:border-primary/40 hover:shadow-[var(--shadow-pop)]"
                >
                  <div className="flex items-center justify-between">
                    <span className="inline-flex size-10 items-center justify-center rounded-lg bg-primary-soft text-primary">
                      <Icon aria-hidden className="size-5" />
                    </span>
                    <Badge tone={ready ? "ok" : "neutral"} mono={!ready}>
                      {ready ? "Đang dùng" : area.milestone}
                    </Badge>
                  </div>
                  <p className="mt-3 font-semibold">{area.label}</p>
                  <p className="mt-0.5 flex-1 text-sm text-muted">{area.description}</p>
                  <span className="mt-3 inline-flex items-center gap-1 text-sm font-medium text-primary">
                    Mở khu <ArrowRight aria-hidden className="size-4 transition-transform duration-150 group-hover:translate-x-0.5" />
                  </span>
                </Link>
              );
            })}
          </div>
        </section>

        <Panel title="Timer sắp tới hạn" actions={<RefreshButton onClick={() => void upcoming.reload()} busy={upcoming.loading} />} flush>
          <DataTable
            rows={upcoming.rows}
            loading={upcoming.loading}
            empty="Chưa có timer nào đang chạy"
            emptyIcon={Clock3}
            minWidth="20rem"
            columns={[
              { key: "nid", label: "Timer", render: (t) => t.NId, mono: true },
              { key: "due", label: "Tới hạn", render: (t) => <Badge tone="accent">{relative(t.NextDueOn)}</Badge> },
              { key: "interval", label: "Nhịp", render: (t) => every(t.IntervalSeconds) },
            ]}
          />
        </Panel>
      </div>
    </>
  );
}
