"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { areas } from "@/lib/areas";

/** Thanh tab gạch chân, dùng chung cho các màn trong một khu. */
export function TabBar({ label, links }: { label: string; links: { href: string; label: string }[] }) {
  const pathname = usePathname();
  return (
    <nav aria-label={label} className="mb-4 flex flex-wrap border-b border-line">
      {links.map((link) => {
        const active = pathname === link.href;
        return (
          <Link
            key={link.href}
            href={link.href}
            aria-current={active ? "page" : undefined}
            className={`-mb-px inline-flex min-h-10 items-center border-b-4 px-4 font-bold transition-colors duration-150 ${
              active ? "border-primary text-primary" : "border-transparent text-muted hover:text-primary-hover"
            }`}
          >
            {link.label}
          </Link>
        );
      })}
    </nav>
  );
}

/** Thanh chọn màn trong một khu có nhiều màn (danh sách `links` của khu). */
export function AreaTabs({ slug, label }: { slug: string; label: string }) {
  return <TabBar label={label} links={areas.find((a) => a.slug === slug)?.links ?? []} />;
}
