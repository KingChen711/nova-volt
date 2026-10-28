"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { areas } from "@/lib/areas";

/** Thanh chọn màn trong một khu có nhiều màn (danh sách `links` của khu). */
export function AreaTabs({ slug, label }: { slug: string; label: string }) {
  const pathname = usePathname();
  const links = areas.find((a) => a.slug === slug)?.links ?? [];
  return (
    <nav aria-label={label} className="mb-5 inline-flex flex-wrap gap-1 rounded-xl border border-line bg-surface p-1 shadow-[var(--shadow-card)]">
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
  );
}
