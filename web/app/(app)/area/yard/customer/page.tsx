"use client";

import { useState } from "react";
import { AreaTabs } from "@/components/AreaTabs";
import { Badge, Button, DataTable, EmptyState, Field, PageHeader, Panel, RefreshButton, ResponsePanel, Toast, useToast } from "@/components/ui";
import { areas, visibleAreas } from "@/lib/areas";
import { useAuth } from "@/lib/auth";
import { useEntitySet } from "@/lib/hooks";
import { postCommand, type MomResult } from "@/lib/mom";

type Trailer = { NId: string; Status: string | null; RoutePool: string | null; LocationNId: string | null; YardPassNId: string | null };
type Movement = { NId: string; TrailerNId: string; FromNId: string | null; ToNId: string; Status: string };

const app = "YardApp";
const next: Record<string, string> = { Created: "Accepted", Accepted: "Picked", Picked: "Dropped" };

/**
 * Xe của hãng vận tải tới lấy thành phẩm: khai xe với route pool, nhận xe vào ô yard gắn với chuyến giao, Move In vào cửa dock,
 * kéo xe từng bước, Move Out ra ô yard của nhóm route, in yard pass, cho xe rời. Lỗi hiện nguyên thân response.
 */
export default function CustomerTrailerPage() {
  const { session } = useAuth();
  const area = areas.find((a) => a.slug === "yard")!;
  const allowed = !session || visibleAreas(session.roles).includes(area);
  const toast = useToast();

  const trailers = useEntitySet<Trailer>(app, "Trailer", { $filter: "RoutePool ne null", $orderby: "NId", $top: "100" });
  const movements = useEntitySet<Movement>(app, "TrailerMovement", { $filter: "Status ne 'Dropped'", $orderby: "CreatedOn" });
  const [form, setForm] = useState({ barcode: "", pool: "KLN", delivery: "", yard: "NV1-CMN-YARD-Y01", dock: "NV1-CMN-DOCK-D01", out: "NV1-CMN-YARD-Y04", pass: "" });
  const [result, setResult] = useState<MomResult | null>(null);
  const [busy, setBusy] = useState(false);
  const set = (key: keyof typeof form) => (value: string) => setForm((f) => ({ ...f, [key]: value }));

  async function run(command: string, body: Record<string, unknown>, done: string) {
    if (!session) {
      return null;
    }

    setBusy(true);
    const r = await postCommand(session.accessToken, app, command, body);
    setBusy(false);
    setResult(r);
    if (r.ok) {
      toast.show(done);
      await Promise.all([trailers.reload(), movements.reload()]);
    }

    return r;
  }

  if (!allowed) {
    return (
      <Panel>
        <EmptyState title="Role của bạn không được vào khu Cổng và dock">
          Khu này dành cho role {area.roles.join(", ")}.
        </EmptyState>
      </Panel>
    );
  }

  const b = form.barcode.trim();
  return (
    <>
      <PageHeader title="Xe của khách" description="Nhận xe, vào và ra dock, yard pass, xe rời">
        <RefreshButton busy={trailers.loading} onClick={() => void Promise.all([trailers.reload(), movements.reload()])} />
      </PageHeader>
      <AreaTabs slug="yard" label="Màn của khu Cổng và dock" />

      <div className="grid gap-5 xl:grid-cols-[26rem_minmax(0,1fr)]">
        <div className="flex flex-col gap-5">
          <Panel title="Xe">
            <div className="flex flex-col gap-3">
              <Field id="ct-barcode" label="Barcode xe" required mono value={form.barcode} onChange={set("barcode")} />
              <Field id="ct-pool" label="Route pool (nơi nhận)" mono value={form.pool} onChange={set("pool")} />
              <Button variant="secondary" busy={busy} disabled={!b} onClick={() => void run("CreateTrailer", { Barcode: b, RoutePool: form.pool }, `Đã khai ${b}`)}>
                Khai xe
              </Button>
              <Field id="ct-delivery" label="Chuyến giao" mono value={form.delivery} onChange={set("delivery")} />
              <Field id="ct-yard" label="Ô yard" mono value={form.yard} onChange={set("yard")} />
              <Button busy={busy} disabled={!b || !form.delivery} onClick={() => void run("ArriveCustomerTrailer", { Barcode: b, DeliveryNId: form.delivery, LocationNId: form.yard }, `${b} đã vào bãi`)}>
                Nhận xe vào bãi
              </Button>
              <Field id="ct-dock" label="Cửa dock" mono value={form.dock} onChange={set("dock")} />
              <Button variant="secondary" busy={busy} disabled={!b} onClick={() => void run("MoveTrailerIn", { Barcode: b, DockNId: form.dock }, "Đã tạo yêu cầu Move In")}>
                Move In
              </Button>
              <Field id="ct-out" label="Ô yard khi ra" mono value={form.out} onChange={set("out")} />
              <Button variant="secondary" busy={busy} disabled={!b} onClick={() => void run("MoveTrailerOut", { Barcode: b, LocationNId: form.out }, "Đã tạo yêu cầu Move Out")}>
                Move Out
              </Button>
              <Button
                variant="secondary"
                busy={busy}
                disabled={!b}
                onClick={() =>
                  void run("IssueYardPass", { Barcode: b }, "Đã in yard pass").then((r) => {
                    const pass = r?.ok && r.body && typeof r.body !== "string" ? (r.body as { NId?: string }).NId : undefined;
                    if (pass) {
                      set("pass")(pass);
                    }
                  })
                }
              >
                In yard pass
              </Button>
              <Field id="ct-pass" label="Yard pass" mono value={form.pass} onChange={set("pass")} />
              <Button busy={busy} disabled={!b || !form.pass} onClick={() => void run("DepartCustomerTrailer", { Barcode: b, YardPassNId: form.pass }, `${b} đã rời nhà máy`)}>
                Trailer Departed
              </Button>
            </div>
          </Panel>
          {result && <ResponsePanel result={result} />}
        </div>

        <div className="flex flex-col gap-5">
          <Panel flush title={<span className="flex items-center gap-2">Yêu cầu kéo đang mở <span className="font-normal text-muted">({movements.rows.length})</span></span>}>
            <DataTable
              rows={movements.rows}
              loading={movements.loading}
              empty="Không có yêu cầu kéo nào đang mở"
              minWidth="36rem"
              columns={[
                { key: "trailer", label: "Xe", mono: true, render: (m) => m.TrailerNId },
                { key: "route", label: "Từ → tới", mono: true, render: (m) => `${m.FromNId ?? "—"} → ${m.ToNId}` },
                { key: "status", label: "Trạng thái", render: (m) => <Badge>{m.Status}</Badge> },
                {
                  key: "next",
                  label: "",
                  render: (m) => (
                    <Button variant="secondary" busy={busy} onClick={() => void run("UpdateTrailerMovement", { MovementNId: m.NId, Status: next[m.Status] }, `${m.TrailerNId}: ${next[m.Status]}`)}>
                      {next[m.Status]}
                    </Button>
                  ),
                },
              ]}
            />
          </Panel>
          <Panel flush title={<span className="flex items-center gap-2">Xe của hãng vận tải <span className="font-normal text-muted">({trailers.rows.length})</span></span>}>
            <DataTable
              rows={trailers.rows}
              loading={trailers.loading}
              empty="Chưa có xe nào khai route pool"
              minWidth="40rem"
              columns={[
                { key: "nid", label: "Xe", mono: true, render: (t) => t.NId },
                { key: "pool", label: "Route pool", mono: true, render: (t) => t.RoutePool ?? "—" },
                { key: "status", label: "Trạng thái", render: (t) => <Badge tone={t.Status === "CustomerLoaded" ? "ok" : "neutral"}>{t.Status ?? "—"}</Badge> },
                { key: "loc", label: "Vị trí", mono: true, render: (t) => t.LocationNId ?? "—" },
                { key: "pass", label: "Yard pass", mono: true, render: (t) => t.YardPassNId ?? "—" },
              ]}
            />
          </Panel>
        </div>
      </div>
      <Toast message={toast.message} onDone={toast.clear} />
    </>
  );
}
