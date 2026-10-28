"use client";

import { KeyRound, Search, Send } from "lucide-react";
import { useState } from "react";
import { Badge, Button, DataTable, Field, Panel, RefreshButton, ResponsePanel, Toast, useToast } from "@/components/ui";
import { useCommand, useEntitySet } from "@/lib/hooks";
import { quote } from "@/lib/mom";
import { AdminHeader } from "../AdminTabs";

type ConfigurationKey = { Id: string; NId: string; Val: string | null; Type: string | null; Category: string | null; LastUpdatedOn: string };

export default function ConfigurationKeysPage() {
  const [category, setCategory] = useState("");
  const keys = useEntitySet<ConfigurationKey>("Foundation", "ConfigurationKey", {
    $filter: category ? `Category eq ${quote(category)}` : undefined,
    $orderby: "NId",
    $top: "200",
  });
  const command = useCommand("Foundation", "SetConfigurationKey");
  const toast = useToast();
  const [form, setForm] = useState({ NId: "", Val: "", Type: "String", Category: "" });

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    const result = await command.send(form);
    if (result?.ok) {
      toast.show(`Đã lưu key ${form.NId}`);
      await keys.reload();
    }
  }

  return (
    <>
      <AdminHeader description="Giá trị luôn là chuỗi. Mỗi chỗ đọc tự quyết khi thiếu key: bỏ qua, dùng mặc định, hay báo lỗi." />
      <div className="grid gap-5 xl:grid-cols-[minmax(0,1fr)_24rem]">
        <Panel
          flush
          title={
            <span className="flex items-center gap-2">
              Danh sách <Badge>{keys.rows.length}</Badge>
            </span>
          }
          actions={
            <div className="flex items-center gap-2">
              <label className="relative">
                <span className="sr-only">Lọc theo Category</span>
                <Search aria-hidden className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted" />
                <input
                  value={category}
                  onChange={(e) => setCategory(e.target.value)}
                  placeholder="Lọc theo Category"
                  className="h-9 w-48 rounded-lg border border-line bg-surface pr-3 pl-9 text-sm"
                />
              </label>
              <RefreshButton onClick={() => void keys.reload()} busy={keys.loading} />
            </div>
          }
        >
          {keys.error && (
            <div className="p-4">
              <ResponsePanel result={keys.error} />
            </div>
          )}
          <DataTable
            rows={keys.rows}
            loading={keys.loading}
            empty={category ? `Không có key nào trong Category “${category}”` : "Chưa có key nào"}
            emptyIcon={KeyRound}
            columns={[
              { key: "nid", label: "NId", render: (k) => <span className="font-medium text-ink">{k.NId}</span>, mono: true },
              { key: "val", label: "Val", render: (k) => k.Val ?? <span className="text-muted">null</span>, mono: true },
              { key: "type", label: "Type", render: (k) => <Badge tone="primary">{k.Type ?? "—"}</Badge> },
              { key: "category", label: "Category", render: (k) => k.Category ?? "—" },
              { key: "updated", label: "Sửa lần cuối (UTC)", render: (k) => k.LastUpdatedOn.slice(0, 16).replace("T", " "), mono: true, className: "text-muted" },
            ]}
          />
        </Panel>

        <Panel title="Đặt key">
          <form onSubmit={submit} className="flex flex-col gap-4">
            <p className="-mt-1 text-xs text-muted">
              Gửi command <span className="font-mono">SetConfigurationKey</span> qua app <span className="font-mono">Foundation</span>.
              Cùng NId thì sửa, chưa có thì tạo.
            </p>
            <Field id="nid" label="NId" required mono placeholder="vd Shift.FreezeHours" value={form.NId} onChange={(NId) => setForm({ ...form, NId })} />
            <Field id="val" label="Val" mono value={form.Val} onChange={(Val) => setForm({ ...form, Val })} />
            <div className="grid grid-cols-2 gap-3">
              <Field id="type" label="Type" value={form.Type} onChange={(Type) => setForm({ ...form, Type })} />
              <Field id="category" label="Category" value={form.Category} onChange={(Category) => setForm({ ...form, Category })} />
            </div>
            <Button type="submit" busy={command.busy}>
              <Send aria-hidden className="size-4" />
              Gửi command
            </Button>
            <ResponsePanel result={command.result} />
          </form>
        </Panel>
      </div>
      <Toast message={toast.message} onDone={toast.clear} />
    </>
  );
}
