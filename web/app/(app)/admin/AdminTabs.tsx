"use client";

import { usePathname } from "next/navigation";
import { TabBar } from "@/components/AreaTabs";
import { PageHeader } from "@/components/ui";
import { areas } from "@/lib/areas";

/** Đầu trang chung của khu Quản trị: tiêu đề khu và thanh chọn màn. */
export function AdminHeader({ description }: { description: string }) {
  const pathname = usePathname();
  const links = areas.find((a) => a.slug === "admin")?.links ?? [];
  const current = links.find((l) => l.href === pathname);
  return (
    <>
      <PageHeader title={current?.label ?? "Quản trị"} description={description} />
      <TabBar label="Màn quản trị" links={links} />
    </>
  );
}
