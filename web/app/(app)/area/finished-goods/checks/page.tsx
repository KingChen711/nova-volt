"use client";

import { CircleCheck, CircleX, Inbox, Lock, SearchCheck } from "lucide-react";
import { useState } from "react";
import { AreaTabs } from "@/components/AreaTabs";
import { Badge, Button, DataTable, EmptyState, Field, PageHeader, Panel, ResponsePanel } from "@/components/ui";
import { areas, visibleAreas } from "@/lib/areas";
import { useAuth } from "@/lib/auth";
import { useCommand, useEntitySet } from "@/lib/hooks";
import { quote } from "@/lib/mom";

type Held = { MtuNId: string; MaterialNId: string | null; Status: string; NonConformanceNId: string | null; NonConformanceStatus: string | null; Depth: number };
type Data = {
  PalletNId: string;
  LocationNId: string | null;
  MaterialNId: string | null;
  Status: string | null;
  Quantity: number;
  StandardQuantity: number | null;
  Totes: number;
  Items: number;
  QuantityOk: boolean;
  TotesOk: boolean;
  ItemsOk: boolean;
  MaterialOk: boolean;
  AvailableOk: boolean;
};

const app = "ShipApp";

function Check({ ok, label }: { ok: boolean; label: string }) {
  const Icon = ok ? CircleCheck : CircleX;
  return (
    <span className={`inline-flex items-center gap-1 ${ok ? "text-ok" : "text-bad"}`} title={label}>
      <Icon aria-hidden className="size-4" />
      <span className="sr-only">{label}: </span>
      {ok ? "đạt" : "lệch"}
    </span>
  );
}

/**
 * Kiểm pallet: cây thành phần (tới 13 tầng) chỉ liệt kê MTU bị giữ hoặc bị hủy, danh sách trống là pallet sạch; năm điều kiểm
 * số liệu; QA Check chạy đúng bước kiểm hàng lỗi mà lấy hàng dùng. Cả ba đi qua procedure của team.
 */
export default function PalletChecksPage() {
  const { session } = useAuth();
  const area = areas.find((a) => a.slug === "finished-goods")!;
  const allowed = !session || visibleAreas(session.roles).includes(area);

  const [input, setInput] = useState("");
  const [pallet, setPallet] = useState("");
  const held = useEntitySet<Held>(app, `PalletHeldItems(PalletNId=${quote(pallet || "-")})`, {});
  const data = useEntitySet<Data>(app, `PalletDataCheck(PalletNId=${quote(pallet || "-")})`, {});
  const qa = useCommand(app, "PalletStatusValidation");

  if (!allowed) {
    return (
      <Panel>
        <EmptyState icon={Lock} title="Role của bạn không được vào khu Thành phẩm">
          Khu này dành cho role {area.roles.join(", ")}.
        </EmptyState>
      </Panel>
    );
  }

  const row = data.rows[0];
  return (
    <>
      <PageHeader icon={SearchCheck} title="Kiểm pallet" description="Hàng bị giữ hoặc hủy trong cây thành phần, năm điều kiểm số liệu, QA Check">
        <form
          className="flex items-end gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            setPallet(input.trim());
          }}
        >
          <Field id="ck-pallet" label="Quét pallet" required mono value={input} onChange={setInput} />
          <Button type="submit" variant="secondary">
            Kiểm
          </Button>
        </form>
      </PageHeader>
      <AreaTabs slug="finished-goods" label="Màn của khu Thành phẩm" />

      {!pallet ? (
        <Panel>
          <EmptyState icon={SearchCheck} title="Quét một pallet để kiểm" />
        </Panel>
      ) : (
        <div className="grid gap-5 xl:grid-cols-[minmax(0,1fr)_24rem]">
          <Panel flush title={<span className="flex items-center gap-2">Hàng bị giữ hoặc hủy <Badge>{held.rows.length}</Badge></span>}>
            {held.error ? (
              <div className="p-4">
                <ResponsePanel result={held.error} />
              </div>
            ) : (
              <DataTable
                rows={held.rows}
                loading={held.loading}
                empty="Kiểm xong: không thành phần nào bị giữ hay bị hủy"
                emptyIcon={Inbox}
                minWidth="40rem"
                columns={[
                  { key: "mtu", label: "MTU", mono: true, render: (h) => h.MtuNId },
                  { key: "mat", label: "Material", mono: true, render: (h) => h.MaterialNId ?? "—" },
                  { key: "status", label: "Trạng thái", render: (h) => <Badge tone={h.Status === "Scrapped" ? "bad" : "warn"}>{h.Status}</Badge> },
                  { key: "nc", label: "NC mở", mono: true, render: (h) => h.NonConformanceNId ?? "—" },
                  { key: "depth", label: "Tầng", mono: true, render: (h) => h.Depth },
                ]}
              />
            )}
          </Panel>

          <div className="flex flex-col gap-5">
            <Panel title="Số liệu pallet">
              {data.error ? (
                <ResponsePanel result={data.error} />
              ) : !row ? (
                <p className="text-sm text-muted">{data.loading ? "Đang đọc…" : `Không có pallet ${pallet}.`}</p>
              ) : (
                <dl className="grid grid-cols-[9rem_1fr] gap-y-2 text-sm">
                  <dt className="text-muted">Part</dt>
                  <dd className="font-mono text-xs">{row.MaterialNId ?? "—"}</dd>
                  <dt className="text-muted">Trạng thái</dt>
                  <dd>
                    <Badge tone={row.Status === "Full" ? "ok" : "neutral"}>{row.Status ?? "—"}</Badge>
                  </dd>
                  <dt className="text-muted">Vị trí</dt>
                  <dd className="font-mono text-xs">{row.LocationNId ?? "—"}</dd>
                  <dt className="text-muted">Số lượng / chuẩn</dt>
                  <dd className="flex items-center gap-2 font-mono text-xs">
                    {row.Quantity} / {row.StandardQuantity ?? "?"} <Check ok={row.QuantityOk} label="Số lượng bằng số chuẩn" />
                  </dd>
                  <dt className="text-muted">Tote</dt>
                  <dd className="flex items-center gap-2 font-mono text-xs">
                    {row.Totes} <Check ok={row.TotesOk} label="Số tote bằng số lượng" />
                  </dd>
                  <dt className="text-muted">Hàng trong tote</dt>
                  <dd className="flex items-center gap-2 font-mono text-xs">
                    {row.Items} <Check ok={row.ItemsOk} label="Số hàng bằng số tote" />
                  </dd>
                  <dt className="text-muted">Cùng part</dt>
                  <dd>
                    <Check ok={row.MaterialOk} label="Mọi hàng cùng part" />
                  </dd>
                  <dt className="text-muted">Mọi hàng Available</dt>
                  <dd>
                    <Check ok={row.AvailableOk} label="Mọi hàng Available" />
                  </dd>
                </dl>
              )}
            </Panel>

            <Panel title="QA Check">
              <Button busy={qa.busy} onClick={() => void qa.send({ PalletNId: pallet })}>
                QA Check
              </Button>
              {qa.result && (
                <div className="mt-4">
                  <ResponsePanel result={qa.result} />
                </div>
              )}
            </Panel>
          </div>
        </div>
      )}
    </>
  );
}
