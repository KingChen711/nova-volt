"use client";

import { Send } from "lucide-react";
import { useState } from "react";
import { AreaTabs } from "@/components/AreaTabs";
import { Badge, Button, DataTable, EmptyState, Field, PageHeader, Panel, RefreshButton, ResponsePanel, Toast, useToast } from "@/components/ui";
import { areas, visibleAreas } from "@/lib/areas";
import { useAuth } from "@/lib/auth";
import { useCommand, useEntitySet } from "@/lib/hooks";
import { quote } from "@/lib/mom";

type Execution = {
  NId: string;
  Serial: string;
  OperationCode: string;
  EquipmentCode: string;
  WorkOrderNId: string;
  ProductMtuNId: string;
  Quantity: number;
  EndTime: string | null;
  StatusId: string;
  StatusCode: string;
  SequenceId: number | null;
};
type Consumption = { NId: string; ComponentMtuNId: string; Quantity: number; OperationNId: string };

const app = "Production";

function time(value: string | null) {
  return value ? new Date(value).toISOString().replace("T", " ").slice(0, 19) + "Z" : "—";
}

/**
 * Tin từ xưởng mà MOM đã ghi: mỗi dòng là một serial qua một bước (LineExecution), kèm nguyên liệu đã trừ cho sản phẩm.
 * Trạng thái dòng tin ở LineDB (NEW, RETRY…) không đọc được từ web: web chỉ đi qua OData của MOM (K10), còn LineDB thuộc
 * phía xưởng. Ghi tay một tin gọi đúng lệnh mà handler gọi; response hiện nguyên văn, kể cả khi Succeeded mà serial nằm
 * trong danh sách chưa ghi (lab L09).
 */
export default function LineMessagesPage() {
  const { session } = useAuth();
  const line = areas.find((a) => a.slug === "line")!;
  const allowed = !session || visibleAreas(session.roles).includes(line);
  const lead = !!session && (session.roles.includes("line-lead") || session.roles.includes("mom-admin"));

  const [serial, setSerial] = useState("");
  const [onlyKo, setOnlyKo] = useState(false);
  const filters = [serial ? `contains(Serial,${quote(serial)})` : null, onlyKo ? "StatusId eq 'KO'" : null].filter(Boolean);
  const executions = useEntitySet<Execution>(app, "LineExecution", {
    $filter: filters.length > 0 ? filters.join(" and ") : undefined,
    $orderby: "CreatedOn desc",
    $top: "50",
  });
  const [picked, setPicked] = useState<Execution | null>(null);
  const consumptions = useEntitySet<Consumption>(app, "MaterialConsumption", {
    $filter: picked ? `ProductMtuNId eq ${quote(picked.ProductMtuNId)}` : "NId eq ''",
  });

  const apply = useCommand(app, "NvProductionExt_ApplyLineResults");
  const toast = useToast();
  const [form, setForm] = useState({ operation: "el010", equipment: "el010_m1_1", serial: "", quantity: "1", status: "OK" });

  async function send() {
    const result = await apply.send({
      Operations: [
        {
          OperationCode: form.operation,
          EquipmentCode: form.equipment,
          Serial: form.serial,
          Quantity: Number(form.quantity),
          StatusId: form.status,
          StatusCode: form.status === "OK" ? "00" : "99",
          EndTime: new Date().toISOString(),
        },
      ],
    });
    if (result?.ok) {
      toast.show(`Đã gửi tin cho ${form.serial}`);
      await executions.reload();
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

  return (
    <>
      <PageHeader title="Tin từ xưởng" description="Kết quả máy đã ghi vào MOM: serial, bước, WO, nguyên liệu đã trừ">
        <div className="flex items-end gap-2">
          <Field id="serial-filter" label="Tìm serial" value={serial} onChange={setSerial} mono placeholder="vd C0001" />
          <label className="flex min-h-touch items-center gap-2 text-sm">
            <input type="checkbox" checked={onlyKo} onChange={(e) => setOnlyKo(e.target.checked)} className="size-4" />
            Chỉ tin KO
          </label>
          <RefreshButton busy={executions.loading} onClick={() => void executions.reload()} />
        </div>
      </PageHeader>
      <AreaTabs slug="line" label="Màn của khu Xưởng" />

      <div className="grid gap-5 xl:grid-cols-[minmax(0,1fr)_24rem]">
        <Panel flush title={<span className="flex items-center gap-2">Đã ghi <span className="font-normal text-muted">({executions.rows.length})</span></span>}>
          {executions.error ? (
            <div className="p-4">
              <ResponsePanel result={executions.error} />
            </div>
          ) : (
            <DataTable
              rows={executions.rows}
              loading={executions.loading}
              empty="Chưa có tin nào được ghi: kiểm trạng thái dòng tin ở LineDB và đường chuyển tin đang bật"
              minWidth="52rem"
              columns={[
                {
                  key: "serial",
                  label: "Serial",
                  mono: true,
                  render: (e) => (
                    <button type="button" className="cursor-pointer text-left text-primary underline underline-offset-2 hover:text-primary-hover" onClick={() => setPicked(e)}>
                      {e.Serial}
                    </button>
                  ),
                },
                { key: "op", label: "Bước", mono: true, render: (e) => e.OperationCode },
                { key: "eq", label: "Máy", mono: true, render: (e) => e.EquipmentCode },
                { key: "wo", label: "WO", mono: true, render: (e) => e.WorkOrderNId },
                { key: "qty", label: "Lượng", mono: true, render: (e) => e.Quantity },
                {
                  key: "status",
                  label: "Kết quả",
                  render: (e) => (
                    <Badge tone={e.StatusId === "KO" ? "bad" : "ok"}>
                      {e.StatusId} {e.StatusCode}
                    </Badge>
                  ),
                },
                { key: "end", label: "Máy xong (UTC)", mono: true, render: (e) => time(e.EndTime) },
                { key: "seq", label: "Dòng LineDB", mono: true, render: (e) => e.SequenceId ?? "tay" },
              ]}
            />
          )}
        </Panel>

        <div className="flex flex-col gap-5">
          <Panel title={picked ? `Nguyên liệu đã trừ cho ${picked.Serial}` : "Nguyên liệu đã trừ"}>
            {!picked ? (
              <p className="text-sm text-muted">Chọn một serial để xem các lô đã trừ cho MTU sản phẩm của nó.</p>
            ) : consumptions.rows.length === 0 ? (
              <p className="text-sm text-muted">Bước này không có nguyên liệu định mức.</p>
            ) : (
              <ul className="flex flex-col gap-2">
                <li className="font-mono text-xs text-muted">{picked.ProductMtuNId}</li>
                {consumptions.rows.map((c) => (
                  <li key={c.NId} className="flex items-center justify-between gap-2 rounded-lg border border-line bg-raised px-3 py-2">
                    <span className="font-mono text-xs break-all">{c.ComponentMtuNId}</span>
                    <span className="font-mono text-xs">{c.Quantity}</span>
                  </li>
                ))}
              </ul>
            )}
          </Panel>

          {lead ? (
            <Panel title="Ghi tay một tin">
              <form
                className="flex flex-col gap-3"
                onSubmit={(e) => {
                  e.preventDefault();
                  void send();
                }}
              >
                <div className="grid grid-cols-2 gap-3">
                  <Field id="op" label="Mã bước" required mono value={form.operation} onChange={(v) => setForm({ ...form, operation: v })} />
                  <Field id="eq" label="Mã máy" required mono hint="mã máy + _ + số line" value={form.equipment} onChange={(v) => setForm({ ...form, equipment: v })} />
                  <Field id="sn" label="Serial sản phẩm" required mono value={form.serial} onChange={(v) => setForm({ ...form, serial: v })} />
                  <Field id="qty" label="Lượng" mono value={form.quantity} onChange={(v) => setForm({ ...form, quantity: v })} />
                </div>
                <label className="flex flex-col gap-1 text-sm font-medium">
                  Kết quả máy
                  <select
                    value={form.status}
                    onChange={(e) => setForm({ ...form, status: e.target.value })}
                    className="min-h-touch rounded-lg border border-field bg-surface px-3 font-mono text-sm"
                  >
                    <option value="OK">OK 00</option>
                    <option value="KO">KO 99</option>
                  </select>
                </label>
                <Button type="submit" busy={apply.busy} disabled={!form.serial}>
                  <Send aria-hidden className="size-4" />
                  Gửi tin
                </Button>
              </form>
              {apply.result && (
                <div className="mt-4">
                  <ResponsePanel result={apply.result} />
                  <p className="mt-2 text-xs text-muted">
                    Succeeded chỉ nói lời gọi chạy xong. Serial nào đã ghi nằm ở ExecutedSerialNumbers, chưa ghi nằm ở NotExecutedOperations kèm mã lỗi.
                  </p>
                </div>
              )}
            </Panel>
          ) : (
            <p className="text-xs text-muted">Chỉ line-lead ghi tay một tin.</p>
          )}
        </div>
      </div>
      <Toast message={toast.message} onDone={toast.clear} />
    </>
  );
}
