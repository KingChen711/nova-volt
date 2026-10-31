"use client";

import { useState } from "react";
import { AreaTabs } from "@/components/AreaTabs";
import { Badge, DataTable, PageHeader, Panel, RefreshButton, StatCard } from "@/components/ui";
import { useEntitySet } from "@/lib/hooks";
import { quote } from "@/lib/mom";

type Row = { TaskNId: string; RouteNId: string; Area: string; MaterialNId: string; KanbanNo: string; Priority: number | null; StandardSeconds: number; CalledOn: string; CallStatus: string | null; TaskStatus: string };

const app = "PullApp";

function tone(status: string | null) {
  return status === "Behind" ? "bad" : status === "HotCall" ? "warn" : status === "OnTime" ? "ok" : "neutral";
}

/** Bảng gọi hàng: một dòng mỗi lệnh gọi linh kiện. Trang chi tiết tính trạng thái mọi dòng theo dòng đang chọn. */
export default function AndonPage() {
  const routes = useEntitySet<Row>(app, "AndonRoutes()", { $orderby: "CalledOn" });
  const [picked, setPicked] = useState("");
  const detail = useEntitySet<Row>(app, `AndonDetail(TaskNId=${quote(picked || "-")})`, { $orderby: "CalledOn" });
  const count = (status: string) => detail.rows.filter((r) => r.CallStatus === status).length;

  return (
    <>
      <PageHeader title="Bảng gọi hàng" description="Chỉ lệnh gọi linh kiện có kanban; lệnh material đặt thẳng không lên bảng">
        <RefreshButton onClick={() => void Promise.all([routes.reload(), detail.reload()])} busy={routes.loading} />
      </PageHeader>
      <AreaTabs slug="andon" label="Màn gọi hàng" />

      <div className="flex flex-col gap-4">
        <Panel flush title={<>Lệnh đang mở <span className="font-normal text-muted">({routes.rows.length})</span></>}>
          <DataTable
            rows={routes.rows}
            loading={routes.loading}
            empty="Không có lệnh gọi linh kiện nào"
            columns={[
              {
                key: "select",
                label: "Lệnh",
                mono: true,
                render: (r) => (
                  <button type="button" onClick={() => setPicked(r.TaskNId)} className="cursor-pointer text-primary underline underline-offset-2 hover:text-primary-hover">
                    {r.TaskNId}
                  </button>
                ),
              },
              { key: "area", label: "Khu", mono: true, render: (r) => r.Area },
              { key: "route", label: "Route", mono: true, render: (r) => r.RouteNId },
              { key: "material", label: "Material", mono: true, render: (r) => r.MaterialNId },
              { key: "priority", label: "Mức", render: (r) => (r.Priority === 1 ? "Gấp" : "Thường") },
            ]}
          />
        </Panel>

        {picked && (
          <>
            <div className="grid gap-4 sm:grid-cols-3">
              <StatCard label="Gọi gấp" value={count("HotCall")} />
              <StatCard label="Trễ" value={count("Behind")} />
              <StatCard label="Đúng hạn" value={count("OnTime")} />
            </div>
            <Panel flush title={`Cùng route với ${picked}`}>
              <DataTable
                rows={detail.rows}
                loading={detail.loading}
                empty="Lệnh này đã đóng"
                columns={[
                  { key: "task", label: "Lệnh", mono: true, render: (r) => r.TaskNId },
                  { key: "kanban", label: "Kanban", mono: true, render: (r) => r.KanbanNo },
                  { key: "standard", label: "Chuẩn (giây)", render: (r) => r.StandardSeconds },
                  { key: "called", label: "Giờ gọi", render: (r) => new Date(r.CalledOn).toLocaleString("vi-VN") },
                  { key: "task-status", label: "Việc chở", render: (r) => <Badge>{r.TaskStatus}</Badge> },
                  { key: "status", label: "Trạng thái gọi", render: (r) => (r.CallStatus ? <Badge tone={tone(r.CallStatus)}>{r.CallStatus}</Badge> : "—") },
                ]}
              />
            </Panel>
          </>
        )}
      </div>
    </>
  );
}
