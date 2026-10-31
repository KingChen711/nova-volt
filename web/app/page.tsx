"use client";

import { useRouter } from "next/navigation";
import { useEffect } from "react";
import { Button } from "@/components/ui";
import { visibleAreas } from "@/lib/areas";
import { useAuth } from "@/lib/auth";

export default function Home() {
  const { session, loading, signIn } = useAuth();
  const router = useRouter();

  useEffect(() => {
    if (session) {
      router.replace(visibleAreas(session.roles)[0].href);
    }
  }, [session, router]);

  return (
    <div className="min-h-dvh">
      <header className="flex h-14 items-center border-b border-line bg-nav px-6 text-lg font-bold text-nav-ink">NovaVolt MOM</header>
      <main className="mx-auto mt-16 max-w-md px-4">
        <section className="rounded-xl border border-line bg-surface p-6">
          <h1 className="text-2xl font-bold">Đăng nhập</h1>
          <p className="mt-2 text-muted">
            Dùng tài khoản của plant <span className="font-mono">{process.env.NEXT_PUBLIC_PLANT}</span>. Bạn sẽ được chuyển
            sang trang đăng nhập Keycloak rồi quay lại đây.
          </p>
          <Button onClick={() => signIn()} busy={loading || !!session} className="mt-5 w-full">
            Đăng nhập bằng Keycloak
          </Button>
          <p className="mt-5 border-t border-line-soft pt-4 text-xs text-muted">
            Tài khoản thử: <span className="font-mono">admin.nv1</span>, <span className="font-mono">op.nv1</span>,{" "}
            <span className="font-mono">qa.nv1</span>… Mật khẩu đặt bằng <span className="font-mono">make keycloak-users</span>.
          </p>
        </section>
        <p className="mt-4 text-center text-xs text-muted">Dự án học tập. Tên nhà máy, hệ thống và mã số là hư cấu.</p>
      </main>
    </div>
  );
}
