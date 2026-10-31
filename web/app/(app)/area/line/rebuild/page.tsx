"use client";

import { useState } from "react";
import { AreaTabs } from "@/components/AreaTabs";
import { Badge, Button, EmptyState, Field, PageHeader, Panel, ResponsePanel } from "@/components/ui";
import { areas, visibleAreas } from "@/lib/areas";
import { useAuth } from "@/lib/auth";
import { useCommand, useEntitySet } from "@/lib/hooks";
import { quote } from "@/lib/mom";

type Container = { NId: string; Status: string | null; Quantity: number; MaterialNId: string | null };

const app = "Production";

/**
 * Dựng lại pallet sau bước đo cuối: quét mã pallet (chưa có thì tạo pallet rỗng), rồi quét từng tote. Tote chỉ nhận khi mọi
 * sản phẩm đã đo đạt, không bị giữ hay hủy, cùng part; tote đầu đặt part và số tote tối đa. Đủ thì nút Completed hiện.
 */
export default function RebuildPalletPage() {
  const { session } = useAuth();
  const line = areas.find((a) => a.slug === "line")!;
  const allowed = !session || visibleAreas(session.roles).includes(line);

  const [palletInput, setPalletInput] = useState("");
  const [pallet, setPallet] = useState("");
  const [tote, setTote] = useState("");
  const [count, setCount] = useState<{ loaded: number; capacity: number } | null>(null);
  const start = useCommand(app, "NvProductionExt_StartPalletRebuild");
  const add = useCommand(app, "NvProductionExt_AddToteToRebuiltPallet");
  const complete = useCommand(app, "NvProductionExt_CompletePalletRebuild");
  const current = useEntitySet<Container>(app, "Mtua", { $filter: `NId eq ${quote(pallet || "-")}` });
  const [last, setLast] = useState<"start" | "add" | "complete">("start");

  async function sendStart() {
    setLast("start");
    const result = await start.send({ PalletNId: palletInput.trim() });
    if (result?.ok) {
      setPallet(palletInput.trim());
      setCount({ loaded: 0, capacity: 0 });
    }
  }

  async function sendTote() {
    setLast("add");
    const result = await add.send({ PalletNId: pallet, ToteNId: tote.trim() });
    if (result?.ok && result.body && typeof result.body !== "string") {
      setCount({ loaded: Number(result.body.Loaded), capacity: Number(result.body.Capacity) });
      setTote("");
    }
  }

  async function sendComplete() {
    setLast("complete");
    const result = await complete.send({ PalletNId: pallet });
    if (result?.ok) {
      await current.reload();
      setPallet("");
      setPalletInput("");
      setCount(null);
    }
  }

  if (!allowed) {
    return (
      <Panel>
        <EmptyState title="Role của bạn không được vào khu Xưởng">
          Khu này dành cho role {line.roles.join(", ")}.
        </EmptyState>
      </Panel>
    );
  }

  const shown = { start: start.result, add: add.result, complete: complete.result }[last];
  return (
    <>
      <PageHeader title="Dựng lại pallet" description="Sau bước đo cuối: quét pallet, quét từng tote đã đo, Completed khi đủ số tote" />
      <AreaTabs slug="line" label="Màn của khu Xưởng" />

      <div className="grid max-w-3xl gap-5">
        <Panel title="Pallet">
          <form
            className="flex items-end gap-2"
            onSubmit={(e) => {
              e.preventDefault();
              void sendStart();
            }}
          >
            <Field id="rb-pallet" label="Quét pallet" required mono value={palletInput} onChange={setPalletInput} />
            <Button type="submit" variant="secondary" busy={start.busy}>
              Mở pallet
            </Button>
          </form>
        </Panel>

        {pallet && count && (
          <Panel
            title={
              <span className="flex items-center gap-2">
                {pallet}
                <Badge tone="accent">
                  Tote đã nạp {count.loaded} / {count.capacity || "?"}
                </Badge>
              </span>
            }
          >
            <form
              className="flex items-end gap-2"
              onSubmit={(e) => {
                e.preventDefault();
                void sendTote();
              }}
            >
              <Field id="rb-tote" label="Quét tote" required mono value={tote} onChange={setTote} />
              <Button type="submit" busy={add.busy} disabled={!tote}>
                Nạp tote
              </Button>
            </form>
            {count.capacity > 0 && count.loaded === count.capacity && (
              <Button className="mt-4" busy={complete.busy} onClick={() => void sendComplete()}>
                Completed
              </Button>
            )}
          </Panel>
        )}

        {shown && <ResponsePanel result={shown} />}
      </div>
    </>
  );
}
