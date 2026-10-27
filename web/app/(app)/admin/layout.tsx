"use client";

import { areas, visibleAreas } from "@/lib/areas";
import { useAuth } from "@/lib/auth";

/** Chặn theo role cả khi gõ thẳng URL, không chỉ ẩn menu. Quyền thật của command vẫn phải kiểm ở service layer. */
export default function AdminLayout({ children }: { children: React.ReactNode }) {
  const { session } = useAuth();
  const admin = areas.find((a) => a.slug === "admin")!;
  if (session && !visibleAreas(session.roles).includes(admin)) {
    return (
      <p role="alert" className="text-bad">
        Role của bạn không được vào khu {admin.label}.
      </p>
    );
  }

  return <>{children}</>;
}
