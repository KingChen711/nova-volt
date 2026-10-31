"use client";

import { ShieldAlert } from "lucide-react";
import { useState } from "react";
import { Badge, Button, DataTable, EmptyState, Field, PageHeader, Panel, RefreshButton, ResponsePanel, Toast, useToast } from "@/components/ui";
import { areas, visibleAreas } from "@/lib/areas";
import { useAuth } from "@/lib/auth";
import { useCommand, useEntitySet } from "@/lib/hooks";

type NcRow = {
  NId: string;
  Status: string;
  Type: string | null;
  DefectNId: string | null;
  StartDate: string | null;
  EndDate: string | null;
  Origin: string | null;
  Serial: string | null;
  OperationCode: string | null;
  StatusCode: string | null;
  SupplierNId: string | null;
  ScrapCode: string | null;
  ScrapTag: string | null;
  MtuCount: number;
};

const app = "Production";
const statuses = ["OPEN", "SCRAP", "USE AS IS"];

function time(value: string | null) {
  return value ? new Date(value).toISOString().replace("T", " ").slice(0, 16) + "Z" : "—";
}

/**
 * Kết luận NC: danh sách đọc qua reading function của team (view SQL cài bằng `make views`; thiếu view thì danh sách
 * trống hoặc lỗi, lab L15). Kết luận SCRAP bắt buộc năm mã và cấp scrap tag; USE AS IS chỉ thả MTU khi MTU hết NC mở.
 * Giữ hàng nghi lỗi hiện nguyên thân lỗi: lý do thật không nằm ở dòng "Bad Request" (lab L13).
 */
export default function QualityPage() {
  const { session } = useAuth();
  const area = areas.find((a) => a.slug === "quality")!;
  const allowed = !session || visibleAreas(session.roles).includes(area);

  const [status, setStatus] = useState("OPEN");
  const list = useEntitySet<NcRow>(app, `NvProductionExt_NonConformanceList(Status='${status === "ALL" ? "" : status}')`, {
    $orderby: "StartDate desc",
    $top: "100",
  });
  const [picked, setPicked] = useState<string[]>([]);

  const sentence = useCommand(app, "NvProductionExt_SentenceNonConformances");
  const suspect = useCommand(app, "NvProductionExt_SuspectHoldMtu");
  const toast = useToast();
  const [form, setForm] = useState({
    status: "SCRAP",
    creating: "",
    charge: "",
    scrap: "",
    problem: "",
    model: "",
    supplier: "",
    notes: "",
  });
  const [hold, setHold] = useState({ mtu: "", defect: "" });

  async function sendSentence() {
    const scrap = form.status === "SCRAP";
    const result = await sentence.send({
      NIds: picked,
      Status: form.status,
      Notes: form.notes || undefined,
      ...(scrap
        ? {
            CreatingCostCenter: form.creating,
            ChargeToCostCenter: form.charge,
            ScrapCode: form.scrap,
            ProblemCode: form.problem,
            ModelCode: form.model,
            SupplierNId: form.supplier || undefined,
          }
        : {}),
    });
    if (result?.ok) {
      toast.show(`Đã kết luận ${picked.length} NC`);
      setPicked([]);
      await list.reload();
    }
  }

  async function sendHold() {
    const result = await suspect.send({ MtuNId: hold.mtu, DefectNId: hold.defect });
    if (result?.ok) {
      toast.show(`Đã giữ ${hold.mtu}`);
      await list.reload();
    }
  }

  if (!allowed) {
    return (
      <Panel>
        <EmptyState title="Role của bạn không được vào khu Chất lượng">
          Khu này dành cho role {area.roles.join(", ")}.
        </EmptyState>
      </Panel>
    );
  }

  const field = (id: keyof typeof form, label: string, required = false) => (
    <Field id={`s-${id}`} label={label} required={required} mono value={form[id]} onChange={(v) => setForm({ ...form, [id]: v })} />
  );

  return (
    <>
      <PageHeader title="Kết luận NC" description="NC theo trạng thái, kết luận SCRAP hoặc USE AS IS, giữ hàng nghi lỗi">
        <div className="flex items-end gap-2">
          <label className="flex flex-col gap-1 text-sm font-medium">
            Trạng thái
            <select
              value={status}
              onChange={(e) => {
                setStatus(e.target.value);
                setPicked([]);
              }}
              className="min-h-touch rounded-lg border border-field bg-surface px-3 font-mono text-sm"
            >
              {statuses.map((s) => (
                <option key={s} value={s}>
                  {s}
                </option>
              ))}
              <option value="ALL">Tất cả</option>
            </select>
          </label>
          <RefreshButton busy={list.loading} onClick={() => void list.reload()} />
        </div>
      </PageHeader>

      <div className="grid gap-5 xl:grid-cols-[minmax(0,1fr)_24rem]">
        <Panel flush title={<span className="flex items-center gap-2">NC <span className="font-normal text-muted">({list.rows.length})</span></span>}>
          {list.error ? (
            <div className="p-4">
              <ResponsePanel result={list.error} />
            </div>
          ) : (
            <DataTable
              rows={list.rows}
              loading={list.loading}
              empty="Không có NC nào. Nếu chắc chắn có NC mà danh sách trống: kiểm view của reading model đã cài chưa (make views)"
              minWidth="60rem"
              columns={[
                {
                  key: "pick",
                  label: "Chọn",
                  render: (n) => (
                    <input
                      type="checkbox"
                      aria-label={`Chọn ${n.NId}`}
                      disabled={n.Status !== "OPEN"}
                      checked={picked.includes(n.NId)}
                      onChange={(e) => setPicked(e.target.checked ? [...picked, n.NId] : picked.filter((p) => p !== n.NId))}
                      className="size-4"
                    />
                  ),
                },
                { key: "nid", label: "NC", mono: true, render: (n) => n.NId },
                {
                  key: "status",
                  label: "Trạng thái",
                  render: (n) => <Badge tone={n.Status === "SCRAP" ? "bad" : n.Status === "OPEN" ? "accent" : "ok"}>{n.Status}</Badge>,
                },
                { key: "origin", label: "Nguồn", render: (n) => n.Origin ?? "—" },
                { key: "serial", label: "Serial", mono: true, render: (n) => n.Serial ?? "—" },
                { key: "op", label: "Bước", mono: true, render: (n) => n.OperationCode ?? "—" },
                { key: "code", label: "Mã máy", mono: true, render: (n) => n.StatusCode ?? "—" },
                { key: "defect", label: "Defect", mono: true, render: (n) => n.DefectNId ?? "—" },
                { key: "mtu", label: "MTU", mono: true, render: (n) => n.MtuCount },
                { key: "tag", label: "Scrap tag", mono: true, render: (n) => n.ScrapTag ?? "—" },
                { key: "start", label: "Mở (UTC)", mono: true, render: (n) => time(n.StartDate) },
              ]}
            />
          )}
        </Panel>

        <div className="flex flex-col gap-5">
          <Panel title={`Kết luận ${picked.length} NC đã chọn`}>
            <form
              className="flex flex-col gap-3"
              onSubmit={(e) => {
                e.preventDefault();
                void sendSentence();
              }}
            >
              <label className="flex flex-col gap-1 text-sm font-medium">
                Kết luận
                <select
                  value={form.status}
                  onChange={(e) => setForm({ ...form, status: e.target.value })}
                  className="min-h-touch rounded-lg border border-field bg-surface px-3 font-mono text-sm"
                >
                  <option value="SCRAP">SCRAP</option>
                  <option value="USE AS IS">USE AS IS</option>
                </select>
              </label>
              {form.status === "SCRAP" && (
                <div className="grid grid-cols-2 gap-3">
                  {field("creating", "Cost center tạo", true)}
                  {field("charge", "Cost center chịu phí", true)}
                  {field("scrap", "Scrap code", true)}
                  {field("problem", "Problem code", true)}
                  {field("model", "Model code", true)}
                  {field("supplier", "Nhà cung cấp")}
                </div>
              )}
              {field("notes", "Ghi chú")}
              <Button type="submit" busy={sentence.busy} disabled={picked.length === 0}>
                Lưu kết luận
              </Button>
            </form>
            {sentence.result && (
              <div className="mt-4">
                <ResponsePanel result={sentence.result} />
              </div>
            )}
          </Panel>

          <Panel title={<span className="flex items-center gap-2"><ShieldAlert aria-hidden className="size-4" /> Giữ hàng nghi lỗi</span>}>
            <form
              className="flex flex-col gap-3"
              onSubmit={(e) => {
                e.preventDefault();
                void sendHold();
              }}
            >
              <Field id="h-mtu" label="MTU" required mono value={hold.mtu} onChange={(v) => setHold({ ...hold, mtu: v })} />
              <Field id="h-defect" label="Defect" required mono value={hold.defect} onChange={(v) => setHold({ ...hold, defect: v })} />
              <Button type="submit" busy={suspect.busy} disabled={!hold.mtu || !hold.defect}>
                Giữ hàng
              </Button>
            </form>
            {suspect.result && (
              <div className="mt-4">
                <ResponsePanel result={suspect.result} />
                <p className="mt-2 text-xs text-muted">Màn của hệ thật chỉ báo “Bad Request”; lý do nằm trong thân lỗi ở trên.</p>
              </div>
            )}
          </Panel>
        </div>
      </div>
      <Toast message={toast.message} onDone={toast.clear} />
    </>
  );
}
