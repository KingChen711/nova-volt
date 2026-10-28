"use client";

import type { LucideIcon } from "lucide-react";
import { CheckCircle2, LoaderCircle, RefreshCw, TriangleAlert } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import type { MomResult } from "@/lib/mom";

export function PageHeader({
  title,
  description,
  icon: Icon,
  children,
}: {
  title: string;
  description?: string;
  icon?: LucideIcon;
  children?: React.ReactNode;
}) {
  return (
    <div className="mb-5 flex flex-wrap items-start justify-between gap-3">
      <div className="flex items-start gap-3">
        {Icon && (
          <span className="mt-0.5 inline-flex size-10 shrink-0 items-center justify-center rounded-lg bg-primary-soft text-primary">
            <Icon aria-hidden className="size-5" />
          </span>
        )}
        <div>
          <h1 className="text-xl font-semibold tracking-tight">{title}</h1>
          {description && <p className="mt-0.5 text-sm text-muted">{description}</p>}
        </div>
      </div>
      {children}
    </div>
  );
}

export function Panel({ title, actions, children, flush }: { title?: React.ReactNode; actions?: React.ReactNode; children: React.ReactNode; flush?: boolean }) {
  return (
    <section className="rounded-xl border border-line bg-surface shadow-[var(--shadow-card)]">
      {(title || actions) && (
        <div className="flex min-h-12 items-center justify-between gap-3 border-b border-line px-4">
          {title && <h2 className="text-sm font-semibold">{title}</h2>}
          {actions}
        </div>
      )}
      <div className={flush ? "" : "p-4"}>{children}</div>
    </section>
  );
}

type Tone = "neutral" | "primary" | "ok" | "warn" | "bad" | "accent";

const tones: Record<Tone, string> = {
  neutral: "bg-raised text-muted ring-line",
  primary: "bg-primary-soft text-primary ring-primary/20",
  ok: "bg-ok-soft text-ok ring-ok/20",
  warn: "bg-warn-soft text-warn ring-warn/20",
  bad: "bg-bad-soft text-bad ring-bad/20",
  accent: "bg-accent-soft text-accent ring-accent/20",
};

export function Badge({ tone = "neutral", children, mono }: { tone?: Tone; children: React.ReactNode; mono?: boolean }) {
  return (
    <span className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-medium ring-1 ring-inset ${tones[tone]} ${mono ? "font-mono" : ""}`}>
      {children}
    </span>
  );
}

/** Thẻ số liệu: con số lớn, nhãn, và một dòng giải thích nó đến từ đâu. */
export function StatCard({
  label,
  value,
  hint,
  icon: Icon,
  tone = "primary",
  loading,
}: {
  label: string;
  value: React.ReactNode;
  hint?: React.ReactNode;
  icon: LucideIcon;
  tone?: Tone;
  loading?: boolean;
}) {
  return (
    <div className="rounded-xl border border-line bg-surface p-4 shadow-[var(--shadow-card)]">
      <div className="flex items-center justify-between">
        <span className="text-sm font-medium text-muted">{label}</span>
        <span className={`inline-flex size-8 items-center justify-center rounded-lg ring-1 ring-inset ${tones[tone]}`}>
          <Icon aria-hidden className="size-4" />
        </span>
      </div>
      <div className="mt-3 font-mono text-3xl font-semibold tracking-tight">
        {loading ? <span className="skeleton inline-block h-8 w-16 align-middle" /> : value}
      </div>
      {hint && <p className="mt-1 text-xs text-muted">{hint}</p>}
    </div>
  );
}

export function EmptyState({ icon: Icon, title, children }: { icon: LucideIcon; title: string; children?: React.ReactNode }) {
  return (
    <div className="flex flex-col items-center px-6 py-10 text-center">
      <span className="inline-flex size-12 items-center justify-center rounded-full bg-raised text-muted ring-1 ring-line">
        <Icon aria-hidden className="size-6" />
      </span>
      <p className="mt-3 font-medium">{title}</p>
      {children && <div className="mt-1 max-w-md text-sm text-muted">{children}</div>}
    </div>
  );
}

export function Field({
  id,
  label,
  hint,
  required,
  value,
  onChange,
  mono,
  placeholder,
}: {
  id: string;
  label: string;
  hint?: string;
  required?: boolean;
  value: string;
  onChange: (value: string) => void;
  mono?: boolean;
  placeholder?: string;
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="text-sm font-medium">
        {label}
        {required && <span className="ml-1 text-bad" aria-hidden>*</span>}
      </label>
      <input
        id={id}
        value={value}
        required={required}
        placeholder={placeholder}
        onChange={(e) => onChange(e.target.value)}
        aria-describedby={hint ? `${id}-hint` : undefined}
        className={`min-h-touch rounded-lg border border-line bg-surface px-3 text-sm shadow-[inset_0_1px_1px_rgb(15_23_42/0.04)] transition-colors duration-150 placeholder:font-sans placeholder:text-muted/70 hover:border-muted/50 focus:border-primary ${mono ? "font-mono" : ""}`}
      />
      {hint && (
        <p id={`${id}-hint`} className="text-xs text-muted">
          {hint}
        </p>
      )}
    </div>
  );
}

export function Button({
  busy,
  children,
  variant = "primary",
  ...props
}: React.ButtonHTMLAttributes<HTMLButtonElement> & { busy?: boolean; variant?: "primary" | "secondary" | "ghost" | "danger" }) {
  const look = {
    primary: "bg-primary text-primary-ink shadow-sm hover:brightness-110 active:brightness-95",
    secondary: "border border-line bg-surface text-ink shadow-sm hover:bg-raised",
    ghost: "text-muted hover:bg-raised hover:text-ink",
    // Ghost cho lệnh phá hủy (hủy WO): chữ màu lỗi thay vì muted.
    danger: "text-bad hover:bg-bad-soft",
  }[variant];
  return (
    <button
      {...props}
      disabled={busy || props.disabled}
      className={`inline-flex min-h-touch cursor-pointer items-center justify-center gap-2 rounded-lg px-4 text-sm font-semibold transition duration-150 disabled:cursor-not-allowed disabled:opacity-60 ${look} ${props.className ?? ""}`}
    >
      {busy && <LoaderCircle aria-hidden className="size-4 animate-spin" />}
      {children}
    </button>
  );
}

export function RefreshButton({ onClick, busy }: { onClick: () => void; busy?: boolean }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-label="Tải lại"
      title="Tải lại"
      className="inline-flex size-touch cursor-pointer items-center justify-center rounded-lg text-muted transition-colors duration-150 hover:bg-raised hover:text-ink"
    >
      <RefreshCw aria-hidden className={`size-4 ${busy ? "animate-spin" : ""}`} />
    </button>
  );
}

/**
 * Nguyên thân response của MOM: mã HTTP, CorrelationId, và JSON đúng như service layer trả. Màn không dịch hay rút gọn
 * lỗi, để người dùng đọc được ErrorCode và câu lỗi thật thay vì chỉ "Bad Request" (lab L13).
 */
export function ResponsePanel({ result }: { result: MomResult | null }) {
  if (!result) {
    return null;
  }

  const Icon = result.ok ? CheckCircle2 : TriangleAlert;
  return (
    <div
      role={result.ok ? "status" : "alert"}
      className={`overflow-hidden rounded-lg border ${result.ok ? "border-ok/30 bg-ok-soft" : "border-bad/30 bg-bad-soft"}`}
    >
      <div className={`flex flex-wrap items-center gap-2 px-3 py-2 text-sm font-semibold ${result.ok ? "text-ok" : "text-bad"}`}>
        <Icon aria-hidden className="size-4" />
        HTTP {result.status}
        <span className="font-normal">{result.ok ? "thành công" : "lỗi"}</span>
      </div>
      {result.correlationId && (
        <p className="border-t border-line/60 bg-surface/60 px-3 py-1.5 text-xs text-muted">
          CorrelationId <span className="font-mono text-ink">{result.correlationId}</span>
        </p>
      )}
      <pre className="max-h-72 overflow-auto border-t border-line/60 bg-surface/80 p-3 font-mono text-xs leading-relaxed whitespace-pre-wrap break-all">
        {typeof result.body === "string" ? result.body : JSON.stringify(result.body, null, 2)}
      </pre>
    </div>
  );
}

/** Thông báo ngắn góc màn hình sau khi một command chạy xong; tự ẩn sau vài giây. */
export function Toast({ message, onDone }: { message: string | null; onDone: () => void }) {
  useEffect(() => {
    if (!message) {
      return;
    }

    const timer = setTimeout(onDone, 3500);
    return () => clearTimeout(timer);
  }, [message, onDone]);

  if (!message) {
    return null;
  }

  return (
    <div role="status" className="toast-in fixed right-4 bottom-4 z-50 flex items-center gap-2 rounded-lg bg-nav px-4 py-3 text-sm text-nav-ink shadow-[var(--shadow-pop)]">
      <CheckCircle2 aria-hidden className="size-4 text-ok" />
      {message}
    </div>
  );
}

export function useToast() {
  const [message, setMessage] = useState<string | null>(null);
  // clear phải ổn định giữa các lần render: Toast dùng nó làm phụ thuộc của timer tự ẩn.
  const clear = useCallback(() => setMessage(null), []);
  return { message, show: setMessage, clear };
}

export type Column<T> = { key: string; label: string; render: (row: T) => React.ReactNode; mono?: boolean; className?: string };

export function DataTable<T>({
  rows,
  columns,
  loading,
  empty,
  emptyIcon,
  minWidth = "36rem",
}: {
  rows: T[];
  columns: Column<T>[];
  loading?: boolean;
  empty: string;
  emptyIcon: LucideIcon;
  minWidth?: string;
}) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full border-collapse text-sm" style={{ minWidth }}>
        <thead>
          <tr className="border-b border-line bg-raised text-left">
            {columns.map((c) => (
              <th key={c.key} scope="col" className="sticky top-0 h-row px-4 text-xs font-semibold tracking-wide whitespace-nowrap text-muted uppercase">
                {c.label}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {loading &&
            [0, 1, 2].map((i) => (
              <tr key={i} className="border-b border-line last:border-0">
                {columns.map((c) => (
                  <td key={c.key} className="h-row px-4">
                    <span className="skeleton block h-3.5 w-3/4" />
                  </td>
                ))}
              </tr>
            ))}
          {!loading && rows.length === 0 && (
            <tr>
              <td colSpan={columns.length}>
                <EmptyState icon={emptyIcon} title={empty} />
              </td>
            </tr>
          )}
          {!loading &&
            rows.map((row, i) => (
              <tr key={i} className="border-b border-line transition-colors duration-150 last:border-0 hover:bg-primary-soft/40">
                {columns.map((c) => (
                  <td key={c.key} className={`h-row px-4 ${c.mono ? "font-mono text-xs" : ""} ${c.className ?? ""}`}>
                    {c.render(row)}
                  </td>
                ))}
              </tr>
            ))}
        </tbody>
      </table>
    </div>
  );
}
