"use client";

import { ChevronRight, LogOut, Menu, X } from "lucide-react";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { Badge } from "@/components/ui";
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

/** Khung của mọi màn sau đăng nhập, kiểu console: thanh trên, side navigation theo role, breadcrumb trên nội dung. */
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
    return <div className="flex min-h-dvh items-center justify-center text-muted">Đang kiểm tra đăng nhập…</div>;
  }

  const visible = visibleAreas(session.roles);
  const current = areaOf(pathname);

  const sidebar = (
    <nav aria-label="Khu" className="h-full overflow-y-auto border-r border-line bg-surface px-5 py-4">
      {groups.map((group) => {
        const items = visible.filter((a) => a.group === group);
        if (items.length === 0) {
          return null;
        }

        return (
          <div key={group} className="mt-5 first:mt-0">
            {!(items.length === 1 && items[0].label === group) && <p className="pb-1 font-bold">{group}</p>}
            {items.map((area) => {
              const active = current?.slug === area.slug;
              return (
                <Link
                  key={area.slug}
                  href={area.href}
                  aria-current={active ? "page" : undefined}
                  className={`block py-1 transition-colors duration-150 ${active ? "font-bold text-primary" : "text-muted hover:text-primary-hover"}`}
                >
                  {area.label}
                </Link>
              );
            })}
          </div>
        );
      })}
    </nav>
  );

  return (
    <div className="min-h-dvh">
      <header className="sticky top-0 z-30 flex h-14 items-center gap-4 border-b border-line bg-nav px-4 text-nav-ink lg:px-6">
        <Link href={visible[0].href} className="text-lg font-bold whitespace-nowrap">
          NovaVolt MOM
        </Link>
        <span className="hidden text-nav-muted sm:inline">
          Plant <span className="font-mono">{plant}</span>
        </span>
        <div className="ml-auto flex items-center gap-4">
          <span className="hidden items-center gap-2 text-nav-muted md:flex" title="Kết nối tới host MOM">
            <span aria-hidden className={`size-2.5 rounded-full ${up === null ? "bg-nav-muted" : up ? "bg-ok" : "bg-bad"}`} />
            {up === null ? "Đang kiểm" : up ? "MOM trực tuyến" : "Mất kết nối MOM"}
          </span>
          {now && <span className="hidden font-mono text-nav-muted tabular-nums sm:inline">{now.toLocaleTimeString("vi-VN", { hour: "2-digit", minute: "2-digit" })}</span>}
          <span className="min-w-0 border-l border-nav-raised pl-4 leading-tight">
            <span className="block truncate font-bold">{session.user}</span>
            <span className="hidden truncate text-xs text-nav-muted sm:block">{businessRoles(session.roles).join(" · ")}</span>
          </span>
          <button
            type="button"
            onClick={() => signOut()}
            aria-label="Đăng xuất"
            title="Đăng xuất"
            className="inline-flex size-touch cursor-pointer items-center justify-center rounded-lg text-nav-muted transition-colors duration-150 hover:bg-nav-raised hover:text-nav-ink"
          >
            <LogOut aria-hidden className="size-4" />
          </button>
        </div>
      </header>

      <aside className="fixed top-14 bottom-0 left-0 z-20 hidden w-70 lg:block">{sidebar}</aside>
      {open && (
        <div className="fixed inset-0 top-14 z-40 lg:hidden">
          <button type="button" aria-label="Đóng menu" className="absolute inset-0 bg-black/40" onClick={() => setOpen(false)} />
          <aside className="relative h-full w-70">{sidebar}</aside>
        </div>
      )}

      <div className="lg:pl-70">
        <div className="sticky top-14 z-10 flex h-10 items-center gap-2 border-b border-line bg-surface px-4 lg:px-6">
          <button
            type="button"
            className="inline-flex size-touch cursor-pointer items-center justify-center rounded-lg hover:bg-raised lg:hidden"
            aria-label={open ? "Đóng menu" : "Mở menu"}
            aria-expanded={open}
            onClick={() => setOpen(!open)}
          >
            {open ? <X aria-hidden className="size-5" /> : <Menu aria-hidden className="size-5" />}
          </button>
          <nav aria-label="Vị trí" className="flex min-w-0 items-center gap-1.5">
            {current && current.group !== current.label && (
              <>
                <span className="hidden text-muted sm:inline">{current.group}</span>
                <ChevronRight aria-hidden className="hidden size-4 text-muted sm:inline" />
              </>
            )}
            <span className="truncate font-bold text-muted">{current?.label ?? "—"}</span>
          </nav>
          {up === false && (
            <span className="ml-auto md:hidden">
              <Badge tone="bad">Mất kết nối MOM</Badge>
            </span>
          )}
        </div>
        <main className="mx-auto max-w-[96rem] p-4 lg:p-6">{children}</main>
      </div>
    </div>
  );
}
