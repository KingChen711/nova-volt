"use client";

import { ListChecks, Lock } from "lucide-react";
import { useState } from "react";
import { Button, EmptyState, Field, PageHeader, Panel, ResponsePanel } from "@/components/ui";
import { areas, visibleAreas } from "@/lib/areas";
import { useAuth } from "@/lib/auth";
import { useCommand } from "@/lib/hooks";

const app = "Production";

/** Danh sách MTU từ ô nhập: mỗi dòng một MTU, hoặc CSV có cột SN (dòng đầu là tên cột). */
function parseList(text: string): string[] {
  const lines = text
    .split(/\r?\n/)
    .map((l) => l.trim())
    .filter(Boolean);
  if (lines.length > 0 && lines[0].split(",").map((c) => c.trim().toUpperCase()).includes("SN")) {
    const index = lines[0].split(",").map((c) => c.trim().toUpperCase()).indexOf("SN");
    return lines.slice(1).map((l) => (l.split(",")[index] ?? "").trim().toUpperCase());
  }

  return lines.map((l) => l.toUpperCase());
}

/**
 * NC hàng loạt: chọn khu trước, rồi quét hoặc dán danh sách MTU. Một dòng sai thì cả danh sách bị từ chối; lý do từng dòng
 * nằm trong response. Giữ hàng tạo một NC OPEN cho cả danh sách; hủy hàng tạo một NC SCRAP mới và không đóng NC OPEN cũ.
 */
export default function BulkNcPage() {
  const { session } = useAuth();
  const area = areas.find((a) => a.slug === "bulk-nc")!;
  const allowed = !session || visibleAreas(session.roles).includes(area);

  const [areaNId, setAreaNId] = useState("");
  const [text, setText] = useState("");
  const validate = useCommand(app, "NvProductionExt_ValidateMtuList");
  const create = useCommand(app, "NvProductionExt_CreateBulkNonConformance");
  const [last, setLast] = useState<"validate" | "create">("validate");
  const mtus = parseList(text);

  if (!allowed) {
    return (
      <Panel>
        <EmptyState icon={Lock} title="Role của bạn không được vào khu Chất lượng">
          Khu này dành cho role {area.roles.join(", ")}.
        </EmptyState>
      </Panel>
    );
  }

  const result = last === "validate" ? validate.result : create.result;

  return (
    <>
      <PageHeader icon={ListChecks} title="NC hàng loạt" description="Giữ hoặc hủy cả một danh sách MTU trong một khu" />
      <div className="grid gap-5 xl:grid-cols-[minmax(0,1fr)_24rem]">
        <Panel title={`Danh sách (${mtus.length} MTU)`}>
          <div className="flex flex-col gap-3">
            <Field id="bulk-area" label="Khu sản xuất" required mono value={areaNId} onChange={setAreaNId} placeholder="vd NV1-B1-L1-CELL" />
            <label className="flex flex-col gap-1 text-sm font-medium" htmlFor="bulk-list">
              MTU, mỗi dòng một mã (hoặc CSV có cột SN)
              <textarea
                id="bulk-list"
                value={text}
                onChange={(e) => setText(e.target.value)}
                rows={10}
                className="rounded-lg border border-line bg-surface p-3 font-mono text-sm"
              />
            </label>
            <div className="flex flex-wrap gap-2">
              <Button
                variant="secondary"
                busy={validate.busy}
                disabled={!areaNId || mtus.length === 0}
                onClick={() => {
                  setLast("validate");
                  void validate.send({ AreaEquipmentNId: areaNId, MtuNIds: mtus });
                }}
              >
                Kiểm danh sách
              </Button>
              <Button
                busy={create.busy}
                disabled={!areaNId || mtus.length === 0}
                onClick={() => {
                  setLast("create");
                  void create.send({ AreaEquipmentNId: areaNId, MtuNIds: mtus });
                }}
              >
                Giữ hàng loạt
              </Button>
              <Button
                variant="danger"
                busy={create.busy}
                disabled={!areaNId || mtus.length === 0}
                onClick={() => {
                  setLast("create");
                  void create.send({ AreaEquipmentNId: areaNId, MtuNIds: mtus, Status: "SCRAP" });
                }}
              >
                Hủy hàng loạt
              </Button>
            </div>
          </div>
        </Panel>
        <Panel title="Kết quả">
          {result ? <ResponsePanel result={result} /> : <p className="text-sm text-muted">Kiểm danh sách trước khi giữ hay hủy.</p>}
        </Panel>
      </div>
    </>
  );
}
