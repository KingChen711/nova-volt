"use client";

import { Settings2 } from "lucide-react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { PageHeader } from "@/components/ui";
import { areas } from "@/lib/areas";

/** Đầu trang chung của khu Quản trị: tiêu đề khu và thanh chọn màn. */
export function AdminHeader({ description }: { description: string }) {
  const pathname = usePathname();
  const links = areas.find((a) => a.slug === "admin")?.links ?? [];
  const current = links.find((l) => l.href === pathname);
  return (
    <>
      <PageHeader icon={Settings2} title={current?.label ?? "Quản trị"} description={description} />
      <nav aria-label="Màn quản trị" className="mb-5 inline-flex flex-wrap gap-1 rounded-xl border border-line bg-surface p-1 shadow-[var(--shadow-card)]">
        {links.map((link) => {
          const active = pathname === link.href;
          return (
            <Link
              key={link.href}
              href={link.href}
              aria-current={active ? "page" : undefined}
              className={`inline-flex min-h-10 items-center rounded-lg px-4 text-sm font-medium transition-colors duration-150 ${
                active ? "bg-primary text-primary-ink shadow-sm" : "text-muted hover:bg-raised hover:text-ink"
              }`}
            >
              {link.label}
            </Link>
          );
        })}
      </nav>
    </>
  );
}
