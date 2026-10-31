"use client";

import { ArrowRight, CircleDot, Plus, ShieldCheck } from "lucide-react";
import { useState } from "react";
import { Badge, Button, DataTable, Field, Panel, RefreshButton, ResponsePanel, Toast, useToast } from "@/components/ui";
import { useCommand, useEntitySet } from "@/lib/hooks";
import { quote } from "@/lib/mom";
import { AdminHeader } from "../AdminTabs";

type Status = { NId: string; Status: string };
type Transition = { NId: string; FromStatus: string | null; ToStatus: string };
type Role = { NId: string; TransitionNId: string; Role: string };

/** State machine là dữ liệu: thêm trạng thái, transition, role theo transition mà không sửa code. */
export default function StateMachinesPage() {
  const [machine, setMachine] = useState("WorkOrder");
  const filter = machine ? `Machine eq ${quote(machine)}` : undefined;
  const statuses = useEntitySet<Status>("Foundation", "StatusDefinition", { $filter: filter, $orderby: "Status" });
  const transitions = useEntitySet<Transition>("Foundation", "StatusTransition", { $filter: filter, $orderby: "NId" });
  const roles = useEntitySet<Role>("Foundation", "TransitionRole", {
    $filter: machine ? `startswith(TransitionNId,${quote(machine + ":")})` : undefined,
  });
  const defineStatus = useCommand("Foundation", "DefineStatus");
  const defineTransition = useCommand("Foundation", "DefineTransition");
  const assignRole = useCommand("Foundation", "AssignTransitionRole");
  const [last, setLast] = useState<"transition" | "role">("transition");
  const toast = useToast();
  const [status, setStatus] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [role, setRole] = useState("");

  async function reloadAll() {
    await Promise.all([statuses.reload(), transitions.reload(), roles.reload()]);
  }

  async function run(send: Promise<{ ok: boolean } | null>, message: string) {
    if ((await send)?.ok) {
      toast.show(message);
      await reloadAll();
    }
  }

  return (
    <>
      <AdminHeader description="Trạng thái và transition nạp bằng command. Transition không gán role thì ai cũng đi được." />
      <div className="mb-5 flex flex-wrap items-end gap-3">
        <div className="w-64">
          <Field id="machine" label="Machine" mono value={machine} onChange={setMachine} />
        </div>
        <RefreshButton onClick={() => void reloadAll()} busy={transitions.loading} />
      </div>

      <div className="grid gap-5 xl:grid-cols-[minmax(0,1fr)_24rem]">
        <div className="flex flex-col gap-5">
          <Panel title="Trạng thái">
            {statuses.rows.length === 0 && !statuses.loading ? (
              <p className="text-sm text-muted">Machine này chưa có trạng thái nào.</p>
            ) : (
              <div className="flex flex-wrap gap-2">
                {statuses.rows.map((s) => (
                  <Badge key={s.NId} tone="primary" mono>
                    <CircleDot aria-hidden className="size-3" />
                    {s.Status}
                  </Badge>
                ))}
              </div>
            )}
          </Panel>
          <Panel
            flush
            title={
              <span className="flex items-center gap-2">
                Transition <span className="font-normal text-muted">({transitions.rows.length})</span>
              </span>
            }
          >
            <DataTable
              rows={transitions.rows}
              loading={transitions.loading}
              empty="Chưa có transition"
              columns={[
                {
                  key: "path",
                  label: "Transition",
                  render: (t) => (
                    <span className="inline-flex items-center gap-2 font-mono text-xs">
                      {t.FromStatus ? <Badge mono>{t.FromStatus}</Badge> : <span className="font-sans text-muted italic">tạo mới</span>}
                      <ArrowRight aria-hidden className="size-3.5 text-muted" />
                      <Badge tone="primary" mono>
                        {t.ToStatus}
                      </Badge>
                    </span>
                  ),
                },
                {
                  key: "roles",
                  label: "Role được đi",
                  render: (t) => {
                    const list = roles.rows.filter((r) => r.TransitionNId === t.NId);
                    return list.length > 0 ? (
                      <span className="flex flex-wrap gap-1">
                        {list.map((r) => (
                          <Badge key={r.NId} tone="warn" mono>
                            <ShieldCheck aria-hidden className="size-3" />
                            {r.Role}
                          </Badge>
                        ))}
                      </span>
                    ) : (
                      <span className="text-muted">mọi người</span>
                    );
                  },
                },
              ]}
            />
          </Panel>
        </div>

        <div className="flex flex-col gap-5">
          <Panel title="Thêm trạng thái">
            <form
              className="flex flex-col gap-3"
              onSubmit={(e) => {
                e.preventDefault();
                void run(defineStatus.send({ Machine: machine, Status: status }), `Đã thêm trạng thái ${status}`);
              }}
            >
              <Field id="status" label="Status" required mono value={status} onChange={setStatus} />
              <Button type="submit" busy={defineStatus.busy}>
                <Plus aria-hidden className="size-4" />
                Thêm trạng thái
              </Button>
              <ResponsePanel result={defineStatus.result} />
            </form>
          </Panel>
          <Panel title="Thêm transition, gán role">
            <form className="flex flex-col gap-3" onSubmit={(e) => e.preventDefault()}>
              <div className="grid grid-cols-2 gap-3">
                <Field id="from" label="Từ" mono value={from} onChange={setFrom} placeholder="(tạo mới)" />
                <Field id="to" label="Sang" required mono value={to} onChange={setTo} />
              </div>
              <Field
                id="role"
                label="Role"
                mono
                value={role}
                onChange={setRole}
                hint="Chỉ cho gán role. Phải trùng role trong Keycloak; gõ sai thì không ai đi được (lab L06)."
              />
              <div className="grid grid-cols-2 gap-2">
                <Button
                  type="button"
                  busy={defineTransition.busy}
                  onClick={() => {
                    setLast("transition");
                    void run(defineTransition.send({ Machine: machine, FromStatus: from || null, ToStatus: to }), "Đã thêm transition");
                  }}
                >
                  Thêm transition
                </Button>
                <Button
                  type="button"
                  variant="secondary"
                  busy={assignRole.busy}
                  onClick={() => {
                    setLast("role");
                    void run(assignRole.send({ Machine: machine, FromStatus: from || null, ToStatus: to, Role: role }), `Đã gán role ${role}`);
                  }}
                >
                  Gán role
                </Button>
              </div>
              <ResponsePanel result={last === "role" ? assignRole.result : defineTransition.result} />
            </form>
          </Panel>
        </div>
      </div>
      <Toast message={toast.message} onDone={toast.clear} />
    </>
  );
}
