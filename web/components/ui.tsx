"use client";

import { CheckCircle2, CircleX, Info, LoaderCircle, RefreshCw, TriangleAlert } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import type { MomResult } from "@/lib/mom";

export function PageHeader({ title, description, children }: { title: string; description?: string; children?: React.ReactNode }) {
  return (
    <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
      <div>
        <h1 className="text-2xl font-bold">{title}</h1>
        {description && <p className="mt-1 text-muted">{description}</p>}
      </div>
      {children}
    </div>
  );
}

export function Panel({ title, actions, children, flush }: { title?: React.ReactNode; actions?: React.ReactNode; children: React.ReactNode; flush?: boolean }) {
  return (
    <section className="overflow-hidden rounded-xl border border-line bg-surface">
      {(title || actions) && (
        <div className="flex min-h-12 items-center justify-between gap-3 px-5 pt-3 pb-2">
          {title && <h2 className="text-lg font-bold">{title}</h2>}
          {actions}
        </div>
      )}
      <div className={flush ? "" : "px-5 pb-4"}>{children}</div>
    </section>
  );
}

type Tone = "neutral" | "primary" | "ok" | "warn" | "bad" | "accent";

const statuses = {
  primary: { icon: Info, text: "text-primary" },
  ok: { icon: CheckCircle2, text: "text-ok" },
  warn: { icon: TriangleAlert, text: "text-warn" },
  accent: { icon: TriangleAlert, text: "text-warn" },
  bad: { icon: CircleX, text: "text-bad" },
};

/** Tone trạng thái vẽ theo status indicator của Cloudscape (icon + chữ màu); `neutral` là badge xám bo 4 px. */
export function Badge({ tone = "neutral", children, mono }: { tone?: Tone; children: React.ReactNode; mono?: boolean }) {
  if (tone === "neutral") {
    return <span className={`inline-flex items-center gap-1 rounded-sm bg-badge px-1.5 text-xs leading-5 font-semibold whitespace-nowrap text-muted ${mono ? "font-mono" : ""}`}>{children}</span>;
  }

  const { icon: Icon, text } = statuses[tone];
  return (
    <span className={`inline-flex items-center gap-1.5 font-normal whitespace-nowrap ${text} ${mono ? "font-mono text-xs" : "text-sm"}`}>
      <Icon aria-hidden className="size-4 shrink-0" />
      {children}
    </span>
  );
}

/** Một cặp nhãn và số trong khung tổng quan. */
export function StatCard({ label, value, hint, loading }: { label: string; value: React.ReactNode; hint?: React.ReactNode; loading?: boolean }) {
  return (
    <div>
      <p className="font-bold text-muted">{label}</p>
      <div className="mt-1 text-2xl font-bold">{loading ? <span className="skeleton inline-block h-7 w-12 align-middle" /> : value}</div>
      {hint && <p className="mt-0.5 text-xs text-muted">{hint}</p>}
    </div>
  );
}

export function EmptyState({ title, children }: { title: string; children?: React.ReactNode }) {
  return (
    <div className="px-6 py-8 text-center">
      <p className="font-bold">{title}</p>
      {children && <div className="mx-auto mt-1 max-w-md text-muted">{children}</div>}
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
    <div className="flex flex-col gap-1">
      <label htmlFor={id} className="font-bold">
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
        className={`min-h-touch rounded-lg border border-field bg-surface px-3 transition-colors duration-150 placeholder:font-sans placeholder:text-muted placeholder:italic focus:border-primary ${mono ? "font-mono" : ""}`}
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
    primary: "border-primary bg-primary text-primary-ink hover:border-primary-hover hover:bg-primary-hover",
    secondary: "border-primary bg-surface text-primary hover:border-primary-hover hover:bg-raised hover:text-primary-hover",
    ghost: "border-transparent text-primary hover:bg-raised hover:text-primary-hover",
    // Lệnh phá hủy (hủy WO): chữ màu lỗi, không viền.
    danger: "border-transparent text-bad hover:bg-bad-soft",
  }[variant];
  return (
    <button
      {...props}
      disabled={busy || props.disabled}
      className={`inline-flex min-h-touch cursor-pointer items-center justify-center gap-2 rounded-[1.25rem] border-2 px-4.5 font-bold transition-colors duration-150 disabled:cursor-not-allowed disabled:border-field disabled:bg-surface disabled:text-disabled ${look} ${props.className ?? ""}`}
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
      className="inline-flex h-touch w-10 cursor-pointer items-center justify-center rounded-[1.25rem] border-2 border-primary text-primary transition-colors duration-150 hover:border-primary-hover hover:bg-raised hover:text-primary-hover"
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

  const Icon = result.ok ? CheckCircle2 : CircleX;
  return (
    <div role={result.ok ? "status" : "alert"} className={`rounded-xl border-2 px-4 py-3 ${result.ok ? "border-ok bg-ok-soft" : "border-bad bg-bad-soft"}`}>
      <div className="flex flex-wrap items-center gap-2 font-bold">
        <Icon aria-hidden className={`size-4 ${result.ok ? "text-ok" : "text-bad"}`} />
        HTTP {result.status}
        <span className="font-normal">{result.ok ? "thành công" : "lỗi"}</span>
      </div>
      {result.correlationId && (
        <p className="mt-1 text-xs text-muted">
          CorrelationId <span className="font-mono text-ink">{result.correlationId}</span>
        </p>
      )}
      <pre className="mt-2 max-h-72 overflow-auto font-mono text-xs leading-relaxed break-all whitespace-pre-wrap">
        {typeof result.body === "string" ? result.body : JSON.stringify(result.body, null, 2)}
      </pre>
    </div>
  );
}

/** Flashbar xanh lá sau khi một command chạy xong; tự ẩn sau vài giây. */
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
    <div role="status" className="toast-in fixed top-28 right-6 left-6 z-50 flex items-center gap-2 rounded-[0.75rem] bg-ok-flash px-4 py-3 text-flash-ink lg:left-[19rem]">
      <CheckCircle2 aria-hidden className="size-4" />
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
  minWidth = "36rem",
}: {
  rows: T[];
  columns: Column<T>[];
  loading?: boolean;
  empty: string;
  minWidth?: string;
}) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full border-collapse" style={{ minWidth }}>
        <thead>
          <tr className="border-b border-line bg-head text-left">
            {columns.map((c) => (
              <th key={c.key} scope="col" className="sticky top-0 h-9 px-5 font-bold whitespace-nowrap text-muted">
                {c.label}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {loading &&
            [0, 1, 2].map((i) => (
              <tr key={i} className="border-b border-line-soft last:border-0">
                {columns.map((c) => (
                  <td key={c.key} className="h-row px-5">
                    <span className="skeleton block h-3.5 w-3/4" />
                  </td>
                ))}
              </tr>
            ))}
          {!loading && rows.length === 0 && (
            <tr>
              <td colSpan={columns.length}>
                <EmptyState title={empty} />
              </td>
            </tr>
          )}
          {!loading &&
            rows.map((row, i) => (
              <tr key={i} className="border-b border-line-soft transition-colors duration-150 last:border-0 hover:bg-raised">
                {columns.map((c) => (
                  <td key={c.key} className={`h-row px-5 ${c.mono ? "font-mono text-[0.8125rem] whitespace-nowrap" : ""} ${c.className ?? ""}`}>
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
