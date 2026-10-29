"use client";

import { Inbox, Lock, Warehouse } from "lucide-react";
import { useState } from "react";
import { AreaTabs } from "@/components/AreaTabs";
import { Badge, Button, DataTable, EmptyState, Field, PageHeader, Panel, RefreshButton, ResponsePanel, Toast, useToast } from "@/components/ui";
import { areas, visibleAreas } from "@/lib/areas";
import { useAuth } from "@/lib/auth";
import { useCommand, useEntitySet } from "@/lib/hooks";
import { quote } from "@/lib/mom";

type Pallet = { NId: string; Status: string | null; Quantity: number; MaterialNId: string | null; EquipmentNId: string | null; ParentMtuaNId: string | null };
type Address = { NId: string; StoreAddress: string; KanbanNId: string | null };
type Fifo = { NId: string; MaterialNId: string | null; LocationNId: string | null; Status: string | null; BuildDate: string | null; LocatedOn: string | null };
type Suggested = { PalletNId: string; LocationNId: string | null; BuildDate: string | null; Quantity: number };

const app = "ShipApp";
const parts = ["NV-FG-KIT-A", "NV-FG-MOD-B60", "NV-FG-MOD-B68"];

function time(value: string | null) {
  return value ? new Date(value).toISOString().replace("T", " ").slice(0, 16) + "Z" : "—";
}

/**
 * Cất kho thành phẩm: quét pallet rồi quét ô (mỗi ô chỉ chạy khi bấm Enter, như màn thật). Ô hợp lệ là store address của part
 * trong bảng địa chỉ; bản ghi FIFO do subscriber ghi sau khi lệnh commit, nên có thể chậm hơn response một nhịp. Đề xuất
 * FIFO đi qua procedure: thiếu procedure hay thiếu key kết nối thì panel hiện nguyên lỗi (lab L17, L18).
 */
export default function StoragePage() {
  const { session } = useAuth();
  const area = areas.find((a) => a.slug === "finished-goods")!;
  const allowed = !session || visibleAreas(session.roles).includes(area);

  const [palletInput, setPalletInput] = useState("");
  const [pallet, setPallet] = useState("");
  const [location, setLocation] = useState("");
  const scanned = useEntitySet<Pallet>(app, "Mtua", { $filter: `NId eq ${quote(pallet || "-")}` });
  const current = scanned.rows[0];
  const addresses = useEntitySet<Address>(app, `StoreAddresses(MaterialNId=${quote(current?.MaterialNId ?? "-")})`, {});
  const fifo = useEntitySet<Fifo>(app, "FifoPallet", { $orderby: "BuildDate,NId", $top: "50" });
  // Pallet Full chưa vào ô kho nào: ở cửa trạm xếp pallet, sau dựng lại (n/a), hoặc ở cửa dock.
  const waiting = useEntitySet<Pallet>(app, "Mtua", {
    $filter: "ContainerType eq 'Pallet' and Status eq 'Full' and ParentMtuaNId eq null and not startswith(EquipmentNId,'NV1-CMN-FGW')",
    $orderby: "CreatedOn desc",
    $top: "20",
  });
  const [part, setPart] = useState(parts[0]);
  const suggested = useEntitySet<Suggested>(app, `SuggestedPallets(MaterialNId=${quote(part)})`, {});

  const store = useCommand(app, "StorePallet");
  const toast = useToast();

  async function sendStore() {
    const result = await store.send({ PalletNId: pallet, LocationNId: location });
    if (result?.ok) {
      toast.show(`Đã cất ${pallet} vào ${location}`);
      setPallet("");
      setPalletInput("");
      setLocation("");
      await Promise.all([fifo.reload(), suggested.reload(), waiting.reload()]);
    }
  }

  if (!allowed) {
    return (
      <Panel>
        <EmptyState icon={Lock} title="Role của bạn không được vào khu Thành phẩm">
          Khu này dành cho role {area.roles.join(", ")}.
        </EmptyState>
      </Panel>
    );
  }

  return (
    <>
      <PageHeader icon={Warehouse} title="Cất kho" description="Quét pallet, quét ô kho theo địa chỉ của part; pallet đã cất có bản ghi FIFO">
        <RefreshButton busy={fifo.loading} onClick={() => void Promise.all([fifo.reload(), suggested.reload()])} />
      </PageHeader>
      <AreaTabs slug="finished-goods" label="Màn của khu Thành phẩm" />

      <div className="grid gap-5 xl:grid-cols-[24rem_minmax(0,1fr)]">
        <div className="flex flex-col gap-5">
          <Panel title="Cất pallet">
            <form
              className="flex flex-col gap-3"
              onSubmit={(e) => {
                e.preventDefault();
                setPallet(palletInput.trim());
              }}
            >
              <Field id="st-pallet" label="Quét pallet" required mono value={palletInput} onChange={setPalletInput} hint="Enter để đọc pallet" />
            </form>
            {pallet && (
              <div className="mt-3 rounded-lg border border-line bg-raised p-3 text-sm">
                {current ? (
                  <dl className="grid grid-cols-[7rem_1fr] gap-y-1">
                    <dt className="text-muted">Trạng thái</dt>
                    <dd>
                      <Badge tone={current.Status === "Full" ? "ok" : "warn"}>{current.Status ?? "—"}</Badge>
                    </dd>
                    <dt className="text-muted">Part</dt>
                    <dd className="font-mono text-xs">{current.MaterialNId ?? "—"}</dd>
                    <dt className="text-muted">Số lượng</dt>
                    <dd className="font-mono text-xs">{current.Quantity}</dd>
                    <dt className="text-muted">Vị trí</dt>
                    <dd className="font-mono text-xs">{current.ParentMtuaNId ? `trong ${current.ParentMtuaNId}` : (current.EquipmentNId ?? "—")}</dd>
                  </dl>
                ) : (
                  <p className="text-muted">Không có pallet {pallet}.</p>
                )}
              </div>
            )}
            <form
              className="mt-3 flex flex-col gap-3"
              onSubmit={(e) => {
                e.preventDefault();
                void sendStore();
              }}
            >
              <Field id="st-location" label="Quét ô kho" required mono value={location} onChange={setLocation} />
              {addresses.rows.length > 0 && (
                <div className="flex flex-wrap gap-2" aria-label="Ô kho gợi ý">
                  {addresses.rows.map((a) => (
                    <button
                      key={a.NId}
                      type="button"
                      onClick={() => setLocation(a.StoreAddress)}
                      className="cursor-pointer rounded-md border border-line px-2 py-1 font-mono text-xs hover:bg-raised"
                    >
                      {a.StoreAddress}
                    </button>
                  ))}
                </div>
              )}
              <Button type="submit" busy={store.busy} disabled={!pallet || !location}>
                Cất pallet
              </Button>
            </form>
            {store.result && (
              <div className="mt-4">
                <ResponsePanel result={store.result} />
              </div>
            )}
          </Panel>

          <Panel title={<span className="flex items-center gap-2">Pallet Full chưa cất <Badge>{waiting.rows.length}</Badge></span>}>
            {waiting.error ? (
              <ResponsePanel result={waiting.error} />
            ) : waiting.rows.length === 0 ? (
              <p className="text-sm text-muted">Không có pallet Full nào ngoài kho.</p>
            ) : (
              <ul className="flex flex-col gap-1" aria-label="Pallet Full chưa cất">
                {waiting.rows.map((p) => (
                  <li key={p.NId}>
                    <button
                      type="button"
                      onClick={() => {
                        setPalletInput(p.NId);
                        setPallet(p.NId);
                      }}
                      className="flex w-full cursor-pointer justify-between gap-2 rounded-md px-2 py-1 text-left font-mono text-xs hover:bg-raised"
                    >
                      <span>{p.NId}</span>
                      <span className="text-muted">{p.MaterialNId}</span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </Panel>

          <Panel
            title="Đề xuất theo FIFO"
            actions={
              <select
                aria-label="Part"
                value={part}
                onChange={(e) => setPart(e.target.value)}
                className="min-h-9 rounded-lg border border-line bg-surface px-2 font-mono text-xs"
              >
                {parts.map((p) => (
                  <option key={p}>{p}</option>
                ))}
              </select>
            }
          >
            {suggested.error ? (
              <ResponsePanel result={suggested.error} />
            ) : suggested.rows.length === 0 ? (
              <p className="text-sm text-muted">Không có pallet Full nào ở ô kho của {part}.</p>
            ) : (
              <ol className="flex flex-col gap-1 text-sm">
                {suggested.rows.map((s, i) => (
                  <li key={`${s.PalletNId}-${i}`} className="flex justify-between gap-2 font-mono text-xs">
                    <span>
                      {i + 1}. {s.PalletNId}
                    </span>
                    <span className="text-muted">
                      {s.LocationNId} · {time(s.BuildDate)}
                    </span>
                  </li>
                ))}
              </ol>
            )}
          </Panel>
        </div>

        <Panel flush title={<span className="flex items-center gap-2">Bản ghi FIFO <Badge>{fifo.rows.length}</Badge></span>}>
          {fifo.error ? (
            <div className="p-4">
              <ResponsePanel result={fifo.error} />
            </div>
          ) : (
            <DataTable
              rows={fifo.rows}
              loading={fifo.loading}
              empty="Chưa có pallet nào được đặt chỗ. Cất xong mà không thấy dòng: subscriber ghi FIFO chạy sau response, tải lại sau một nhịp"
              emptyIcon={Inbox}
              minWidth="44rem"
              columns={[
                { key: "nid", label: "Pallet", mono: true, render: (f) => f.NId },
                { key: "part", label: "Part", mono: true, render: (f) => f.MaterialNId ?? "—" },
                { key: "loc", label: "Vị trí", mono: true, render: (f) => f.LocationNId ?? "—" },
                { key: "status", label: "Trạng thái", render: (f) => <Badge tone={f.Status === "Full" ? "ok" : "neutral"}>{f.Status ?? "—"}</Badge> },
                { key: "build", label: "Ngày đóng (UTC)", mono: true, render: (f) => time(f.BuildDate) },
                { key: "at", label: "Đặt chỗ (UTC)", mono: true, render: (f) => time(f.LocatedOn) },
              ]}
            />
          )}
        </Panel>
      </div>
      <Toast message={toast.message} onDone={toast.clear} />
    </>
  );
}
