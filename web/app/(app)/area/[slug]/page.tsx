"use client";

import { CircleDashed, Lock, MapPinned, Milestone } from "lucide-react";
import { useParams } from "next/navigation";
import { Badge, EmptyState, PageHeader, Panel } from "@/components/ui";
import { areas, visibleAreas } from "@/lib/areas";
import { useAuth } from "@/lib/auth";

/** Màn của một khu. Mốc làm luồng nào thì làm màn của luồng đó (scope v2 §5); trước đó khu cho biết sẽ có gì. */
export default function AreaPage() {
  const { slug } = useParams<{ slug: string }>();
  const { session } = useAuth();
  const area = areas.find((a) => a.slug === slug);

  if (!area) {
    return <EmptyState icon={MapPinned} title={`Không có khu “${slug}”`} />;
  }

  if (session && !visibleAreas(session.roles).includes(area)) {
    return (
      <Panel>
        <EmptyState icon={Lock} title={`Role của bạn không được vào khu ${area.label}`}>
          Khu này dành cho role {area.roles.join(", ")}.
        </EmptyState>
      </Panel>
    );
  }

  return (
    <>
      <PageHeader icon={area.icon} title={area.label} description={area.description}>
        <Badge tone="accent">
          <Milestone aria-hidden className="size-3.5" />
          Mốc <span className="font-mono">{area.milestone}</span>
        </Badge>
      </PageHeader>

      <div className="grid gap-4 lg:grid-cols-[1fr_20rem]">
        <Panel title="Các màn của khu">
          <ol className="grid gap-3 sm:grid-cols-2">
            {area.screens.map((screen, i) => (
              <li key={screen} className="flex items-center gap-3 rounded-lg border border-dashed border-line bg-raised px-3 py-3">
                <span className="inline-flex size-7 shrink-0 items-center justify-center rounded-full bg-surface font-mono text-xs text-muted ring-1 ring-line">
                  {i + 1}
                </span>
                <span className="flex-1 text-sm font-medium">{screen}</span>
                <CircleDashed aria-label="Chưa dựng" className="size-4 text-muted" />
              </li>
            ))}
          </ol>
          <p className="mt-4 text-sm text-muted">
            Màn được dựng cùng luồng nghiệp vụ của nó ở mốc {area.milestone}: luồng chạy được ở backend thì màn mới có dữ
            liệu thật để hiện.
          </p>
        </Panel>
        <Panel title="Ai dùng khu này">
          <div className="flex flex-wrap gap-2">
            {area.roles.length > 0 ? (
              [...area.roles, "mom-admin"].map((role) => (
                <Badge key={role} tone="primary" mono>
                  {role}
                </Badge>
              ))
            ) : (
              <span className="text-sm text-muted">Mọi người dùng đã đăng nhập</span>
            )}
          </div>
          <p className="mt-3 text-xs text-muted">Menu và đường dẫn theo realm role trong token Keycloak.</p>
        </Panel>
      </div>
    </>
  );
}
