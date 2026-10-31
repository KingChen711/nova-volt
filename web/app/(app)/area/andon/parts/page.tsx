"use client";

import { AreaTabs } from "@/components/AreaTabs";
import { DataTable, PageHeader, Panel, RefreshButton } from "@/components/ui";
import { useEntitySet } from "@/lib/hooks";

type Kanban = { NId: string; MaterialNId: string; DockCode: string | null; SupplierCode: string | null; KanbanNo: string; QuantityPerContainer: number; EffectiveFrom: string; EffectiveTo: string | null };
type Address = { NId: string; MaterialNId: string; KanbanNId: string | null; StoreAddress: string; DeliveryAddress: string | null; DeliveryRouteNId: string | null; EffectiveFrom: string | null; EffectiveTo: string | null };
type Route = { NId: string; Area: string | null; Buffer1: string | null; Buffer2: string | null; Buffer3: string | null; Buffer4: string; Minutes1: number; Minutes2: number; Minutes3: number };

/** Dữ liệu gọi hàng để xem: kanban từ hệ ngoài, part address, route bốn tier. */
export default function PartsPage() {
  const kanbans = useEntitySet<Kanban>("PullApp", "Kanban", { $orderby: "MaterialNId", $top: "200" });
  const addresses = useEntitySet<Address>("PullApp", "PartAddress", { $filter: "DeliveryRouteNId ne null", $orderby: "MaterialNId", $top: "200" });
  const routes = useEntitySet<Route>("PullApp", "DeliveryRoute", { $orderby: "NId", $top: "200" });

  return (
    <>
      <PageHeader title="Kanban và route" description="Part gọi được khi kanban và part address còn hiệu lực, route có ô giao ở khu">
        <RefreshButton onClick={() => void Promise.all([kanbans.reload(), addresses.reload(), routes.reload()])} busy={kanbans.loading} />
      </PageHeader>
      <AreaTabs slug="andon" label="Màn gọi hàng" />

      <div className="flex flex-col gap-4">
        <Panel flush title={<>Route <span className="font-normal text-muted">({routes.rows.length})</span></>}>
          <DataTable
            rows={routes.rows}
            loading={routes.loading}
            empty="Chưa có route"
            columns={[
              { key: "route", label: "Route", mono: true, render: (r) => r.NId },
              { key: "area", label: "Khu", mono: true, render: (r) => r.Area ?? "—" },
              { key: "t1", label: "Tier 1", mono: true, render: (r) => r.Buffer1 ?? "—" },
              { key: "t2", label: "Tier 2", mono: true, render: (r) => r.Buffer2 ?? "—" },
              { key: "t3", label: "Tier 3", mono: true, render: (r) => r.Buffer3 ?? "—" },
              { key: "t4", label: "Tier 4", mono: true, render: (r) => r.Buffer4 },
              { key: "minutes", label: "Phút từng chặng", render: (r) => `${r.Minutes1} / ${r.Minutes2} / ${r.Minutes3}` },
            ]}
          />
        </Panel>
        <Panel flush title={<>Part address <span className="font-normal text-muted">({addresses.rows.length})</span></>}>
          <DataTable
            rows={addresses.rows}
            loading={addresses.loading}
            empty="Chưa có part address gắn route"
            columns={[
              { key: "material", label: "Material", mono: true, render: (a) => a.MaterialNId },
              { key: "kanban", label: "Kanban", mono: true, render: (a) => a.KanbanNId ?? "—" },
              { key: "store", label: "Ô kho", mono: true, render: (a) => a.StoreAddress },
              { key: "delivery", label: "Ô giao", mono: true, render: (a) => a.DeliveryAddress ?? "—" },
              { key: "route", label: "Route", mono: true, render: (a) => a.DeliveryRouteNId },
              { key: "from", label: "Hiệu lực", mono: true, render: (a) => `${a.EffectiveFrom ?? "—"} → ${a.EffectiveTo ?? "…"}` },
            ]}
          />
        </Panel>
        <Panel flush title={<>Kanban <span className="font-normal text-muted">({kanbans.rows.length})</span></>}>
          <DataTable
            rows={kanbans.rows}
            loading={kanbans.loading}
            empty="Chưa có kanban"
            columns={[
              { key: "material", label: "Material", mono: true, render: (k) => k.MaterialNId },
              { key: "no", label: "Kanban", mono: true, render: (k) => k.KanbanNo },
              { key: "dock", label: "Dock", mono: true, render: (k) => k.DockCode ?? "—" },
              { key: "supplier", label: "Nhà cung cấp", mono: true, render: (k) => k.SupplierCode ?? "—" },
              { key: "quantity", label: "Số mỗi thùng", render: (k) => k.QuantityPerContainer },
              { key: "from", label: "Hiệu lực", mono: true, render: (k) => `${k.EffectiveFrom} → ${k.EffectiveTo ?? "…"}` },
            ]}
          />
        </Panel>
      </div>
    </>
  );
}
