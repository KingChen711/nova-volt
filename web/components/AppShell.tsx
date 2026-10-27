"use client";

import { ChevronRight, LogOut, Menu, X, Zap } from "lucide-react";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { areaOf, businessRoles, groups, visibleAreas } from "@/lib/areas";
import { useAuth } from "@/lib/auth";

const plant = process.env.NEXT_PUBLIC_PLANT;

/** Trạng thái kết nối tới host MOM: hỏi /healthz mỗi 30 giây. */
function useMomHealth() {
  const [up, setUp] = useState<boolean | null>(null);
  useEffect(() => {
    let alive = true;
    const check = () =>
      fetch(`${process.env.NEXT_PUBLIC_MOM_URL}/healthz`)
        .then((r) => alive && setUp(r.ok))
        .catch(() => alive && setUp(false));
    void check();
    const timer = setInterval(check, 30_000);
    return () => {
      alive = false;
      clearInterval(timer);
    };
  }, []);
  return up;
}

function useClock() {
  const [now, setNow] = useState<Date | null>(null);
  useEffect(() => {
    setNow(new Date());
    const timer = setInterval(() => setNow(new Date()), 15_000);
    return () => clearInterval(timer);
  }, []);
  return now;
}

/** Khung của mọi màn sau đăng nhập: sidebar chỉ có khu mà role của người dùng được thấy. */
export function AppShell({ children }: { children: React.ReactNode }) {
  const { session, loading, signOut } = useAuth();
  const router = useRouter();
  const pathname = usePathname();
  const [open, setOpen] = useState(false);
  const up = useMomHealth();
  const now = useClock();

  useEffect(() => {
    if (!loading && !session) {
      router.replace("/");
    }
  }, [loading, session, router]);

  useEffect(() => setOpen(false), [pathname]);

  if (!session) {
    return (
      <div className="flex min-h-dvh items-center justify-center text-sm text-muted">Đang kiểm tra đăng nhập…</div>
    );
  }

  const visible = visibleAreas(session.roles);
  const current = areaOf(pathname);
  const initials = session.user.split(/[.\-_]/).map((p) => p[0]?.toUpperCase()).join("").slice(0, 2);

  const sidebar = (
    <div className="flex h-full flex-col bg-nav text-nav-ink">
      <div className="flex h-16 items-center gap-3 px-5">
        <span className="inline-flex size-9 items-center justify-center rounded-lg bg-nav-active text-nav shadow-[0_0_0_4px_rgb(245_158_11/0.15)]">
          <Zap aria-hidden className="size-5" strokeWidth={2.5} />
        </span>
        <div className="leading-tight">
          <p className="font-semibold tracking-tight">NovaVolt MOM</p>
          <p className="text-xs text-nav-muted">
            Plant <span className="font-mono">{plant}</span>
          </p>
        </div>
      </div>
      <nav aria-label="Khu" className="flex-1 overflow-y-auto px-3 pb-4">
        {groups.map((group) => {
          const items = visible.filter((a) => a.group === group);
          if (items.length === 0) {
            return null;
          }

          return (
            <div key={group} className="mt-4 first:mt-1">
              <p className="px-3 pb-1 text-[0.6875rem] font-semibold tracking-wider text-nav-muted uppercase">{group}</p>
              {items.map((area) => {
                const active = current?.slug === area.slug;
                const Icon = area.icon;
                return (
                  <Link
                    key={area.slug}
                    href={area.href}
                    aria-current={active ? "page" : undefined}
                    className={`group relative flex min-h-touch items-center gap-3 rounded-lg px-3 text-sm font-medium transition-colors duration-150 ${
                      active ? "bg-nav-raised text-white" : "text-nav-ink/80 hover:bg-nav-raised/60 hover:text-white"
                    }`}
                  >
                    {active && <span aria-hidden className="absolute top-2 bottom-2 left-0 w-1 rounded-r bg-nav-active" />}
                    <Icon aria-hidden className={`size-[1.125rem] shrink-0 ${active ? "text-nav-active" : "text-nav-muted group-hover:text-nav-ink"}`} />
                    {area.label}
                  </Link>
                );
              })}
            </div>
          );
        })}
      </nav>
      <div className="border-t border-white/10 p-3">
        <div className="flex items-center gap-3 rounded-lg px-2 py-2">
          <span className="inline-flex size-9 shrink-0 items-center justify-center rounded-full bg-primary text-sm font-semibold text-white">
            {initials}
          </span>
          <div className="min-w-0 flex-1 leading-tight">
            <p className="truncate text-sm font-medium">{session.user}</p>
            <p className="truncate font-mono text-[0.6875rem] text-nav-muted">{businessRoles(session.roles).join(" · ")}</p>
          </div>
          <button
            type="button"
            onClick={() => signOut()}
            aria-label="Đăng xuất"
            title="Đăng xuất"
            className="inline-flex size-touch cursor-pointer items-center justify-center rounded-lg text-nav-muted transition-colors duration-150 hover:bg-nav-raised hover:text-white"
          >
            <LogOut aria-hidden className="size-4" />
          </button>
        </div>
      </div>
    </div>
  );

  return (
    <div className="min-h-dvh lg:pl-64">
      <aside className="fixed inset-y-0 left-0 z-30 hidden w-64 lg:block">{sidebar}</aside>
      {open && (
        <div className="fixed inset-0 z-40 lg:hidden">
          <button type="button" aria-label="Đóng menu" className="absolute inset-0 bg-black/40" onClick={() => setOpen(false)} />
          <aside className="relative h-full w-72 shadow-[var(--shadow-pop)]">{sidebar}</aside>
        </div>
      )}
      <header className="sticky top-0 z-20 flex h-16 items-center gap-3 border-b border-line bg-surface/85 px-4 backdrop-blur lg:px-8">
        <button
          type="button"
          className="inline-flex size-touch items-center justify-center rounded-lg hover:bg-raised lg:hidden"
          aria-label={open ? "Đóng menu" : "Mở menu"}
          aria-expanded={open}
          onClick={() => setOpen(!open)}
        >
          {open ? <X aria-hidden className="size-5" /> : <Menu aria-hidden className="size-5" />}
        </button>
        <nav aria-label="Vị trí" className="flex min-w-0 items-center gap-1.5 text-sm">
          {current && current.group !== current.label && (
            <>
              <span className="hidden text-muted sm:inline">{current.group}</span>
              <ChevronRight aria-hidden className="hidden size-4 text-muted sm:inline" />
            </>
          )}
          <span className="truncate font-semibold">{current?.label ?? "—"}</span>
        </nav>
        <div className="ml-auto flex items-center gap-4 text-sm">
          <span className="hidden items-center gap-2 sm:flex" title="Kết nối tới host MOM">
            <span
              aria-hidden
              className={`size-2.5 rounded-full ${up === null ? "bg-muted" : up ? "bg-ok shadow-[0_0_0_3px_rgb(34_197_94/0.2)]" : "bg-bad shadow-[0_0_0_3px_rgb(239_68_68/0.2)]"}`}
            />
            <span className="text-muted">{up === null ? "Đang kiểm" : up ? "MOM trực tuyến" : "Mất kết nối MOM"}</span>
          </span>
          {now && (
            <span className="font-mono text-muted tabular-nums">
              {now.toLocaleTimeString("vi-VN", { hour: "2-digit", minute: "2-digit" })}
            </span>
          )}
        </div>
      </header>
      <main className="mx-auto max-w-[96rem] p-4 lg:p-8">{children}</main>
    </div>
  );
}
