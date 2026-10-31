"use client";

import { Timer } from "lucide-react";
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
        title={`Xin chào, ${hello}`}
        description="Tình trạng cấu hình nền của plant và lối vào các khu bạn được làm việc."
      />

      <Panel title="Cấu hình nền">
        <div className="grid gap-5 sm:grid-cols-2 xl:grid-cols-5">
          <StatCard label="Configuration key" value={keys ?? "—"} loading={keys === null} hint="Giá trị cấu hình dạng chuỗi" />
          <StatCard label="Signal rule đang bật" value={rules ?? "—"} loading={rules === null} hint="Gọi command theo timer hoặc event" />
          <StatCard label="Timer đang chạy" value={timers ?? "—"} loading={timers === null} hint="Tạo lúc chạy, có mã định danh" />
          <StatCard label="Transition" value={transitions ?? "—"} loading={transitions === null} hint="State machine dạng dữ liệu" />
          <StatCard label="Subscription theo plant" value={subscriptions ?? "—"} loading={subscriptions === null} hint="Không tính loại khai sẵn trong model" />
        </div>
      </Panel>

      <div className="mt-4 grid gap-4 xl:grid-cols-[minmax(0,1fr)_26rem]">
        <Panel title={<>Khu của bạn <span className="font-normal text-muted">({areas.length})</span></>} flush>
          <DataTable
            rows={areas}
            empty="Role của bạn chưa được vào khu nào"
            columns={[
              {
                key: "area",
                label: "Khu",
                className: "whitespace-nowrap",
                render: (a) => (
                  <Link href={a.href} className="text-primary underline underline-offset-2 hover:text-primary-hover">
                    {a.label}
                  </Link>
                ),
              },
              { key: "description", label: "Mô tả", render: (a) => a.description },
              { key: "milestone", label: "Mốc", render: (a) => <Badge mono>{a.milestone}</Badge> },
            ]}
          />
        </Panel>

        <Panel title="Timer sắp tới hạn" actions={<RefreshButton onClick={() => void upcoming.reload()} busy={upcoming.loading} />} flush>
          <DataTable
            rows={upcoming.rows}
            loading={upcoming.loading}
            empty="Chưa có timer nào đang chạy"
           
            minWidth="20rem"
            columns={[
              { key: "nid", label: "Timer", render: (t) => t.NId, mono: true },
              { key: "due", label: "Tới hạn", render: (t) => <Badge>{relative(t.NextDueOn)}</Badge> },
              { key: "interval", label: "Nhịp", render: (t) => every(t.IntervalSeconds) },
            ]}
          />
        </Panel>
      </div>
    </>
  );
}
