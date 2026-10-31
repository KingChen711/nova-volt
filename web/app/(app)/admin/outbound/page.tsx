"use client";

import { AdminHeader } from "../AdminTabs";
import { Badge, DataTable, Panel } from "@/components/ui";
import { useEntitySet } from "@/lib/hooks";

type Block = { NId: string; FirstDaysToSkip: number; WeekInstance: number; DayOfWeek: string | null };
type Item = { NId: string; MessageClass: string; WorkOrderNId: string | null; Sent: boolean; SentOn: string | null; CreatedOn: string };
type Line = { NId: string; NonConformanceNId: string; MtuNId: string; Status: string; Reason: string | null };

const app = "OutApp";

function week(block: Block): string {
  if (block.WeekInstance === 0) {
    return "—";
  }

  return `${block.WeekInstance === 9 ? "Cuối tháng" : `Lần ${block.WeekInstance}`}, ${block.DayOfWeek}`;
}

/** Chỗ xem đầu tiên khi "tin ERP không đi": lịch chặn, tin đang chờ, dòng scrap bị loại. */
export default function OutboundPage() {
  const blocks = useEntitySet<Block>(app, "MessageBlock", { $orderby: "NId" });
  const items = useEntitySet<Item>(app, "OutboundQueueItem", { $orderby: "CreatedOn desc", $top: "100" });
  const errors = useEntitySet<Line>(app, "ScrapMessageLine", { $filter: "Status eq 'ERROR'", $top: "100" });

  return (
    <>
      <AdminHeader description="Ngày bị chặn thì tin vào hàng đợi, timer mỗi giờ gửi tối đa 20 tin khi hết chặn. Lịch tính theo giờ miền Đông Mỹ" />

      <div className="flex flex-col gap-4">
        <Panel flush title={<>Lịch chặn <span className="font-normal text-muted">({blocks.rows.length})</span></>}>
          <DataTable
            rows={blocks.rows}
            loading={blocks.loading}
            empty="Không có lịch chặn: mọi tin đi ngay"
            columns={[
              { key: "class", label: "Loại tin", mono: true, render: (b) => b.NId },
              { key: "days", label: "Chặn đầu tháng", render: (b) => (b.FirstDaysToSkip > 0 ? `Ngày 1–${b.FirstDaysToSkip}` : "—") },
              { key: "week", label: "Chặn theo thứ", render: week },
            ]}
          />
        </Panel>
        <Panel flush title={<>Hàng đợi <span className="font-normal text-muted">({items.rows.length})</span></>}>
          <DataTable
            rows={items.rows}
            loading={items.loading}
            empty="Hàng đợi trống"
            columns={[
              { key: "nid", label: "Tin", mono: true, render: (i) => i.NId },
              { key: "class", label: "Loại", mono: true, render: (i) => i.MessageClass },
              { key: "created", label: "Vào hàng đợi", render: (i) => new Date(i.CreatedOn).toLocaleString("vi-VN") },
              { key: "state", label: "Trạng thái", render: (i) => <Badge tone={i.Sent ? "ok" : "warn"}>{i.Sent ? "Đã gửi" : "Đang chờ"}</Badge> },
            ]}
          />
        </Panel>
        <Panel flush title={<>Dòng scrap bị loại <span className="font-normal text-muted">({errors.rows.length})</span></>}>
          <DataTable
            rows={errors.rows}
            loading={errors.loading}
            empty="Không có dòng nào bị loại"
            columns={[
              { key: "nc", label: "NC", mono: true, render: (l) => l.NonConformanceNId },
              { key: "mtu", label: "MTU", mono: true, render: (l) => l.MtuNId },
              { key: "reason", label: "Lý do", render: (l) => l.Reason },
            ]}
          />
        </Panel>
      </div>
    </>
  );
}
