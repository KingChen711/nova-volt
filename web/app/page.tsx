"use client";

import { Activity, LogIn, ShieldCheck, Workflow, Zap } from "lucide-react";
import { useRouter } from "next/navigation";
import { useEffect } from "react";
import { Button } from "@/components/ui";
import { visibleAreas } from "@/lib/areas";
import { useAuth } from "@/lib/auth";

const points = [
  { icon: Workflow, text: "Lệnh sản xuất theo ca, tin từ xưởng, hàng lỗi, đóng gói, xuất hàng" },
  { icon: ShieldCheck, text: "Mỗi vai chỉ thấy khu của mình, theo role trong token" },
  { icon: Activity, text: "Lỗi hiện nguyên mã và câu, kèm CorrelationId để lần trace" },
];

export default function Home() {
  const { session, loading, signIn } = useAuth();
  const router = useRouter();

  useEffect(() => {
    if (session) {
      router.replace(visibleAreas(session.roles)[0].href);
    }
  }, [session, router]);

  return (
    <main className="grid min-h-dvh lg:grid-cols-[1.1fr_1fr]">
      <section className="blueprint relative hidden flex-col justify-between p-12 text-nav-ink lg:flex">
        <div className="flex items-center gap-3">
          <span className="inline-flex size-10 items-center justify-center rounded-lg bg-nav-active text-nav">
            <Zap aria-hidden className="size-5" strokeWidth={2.5} />
          </span>
          <span className="text-lg font-semibold tracking-tight">NovaVolt MOM</span>
        </div>
        <div className="max-w-lg">
          <p className="font-mono text-sm text-nav-active">Plant {process.env.NEXT_PUBLIC_PLANT}</p>
          <h1 className="mt-3 text-4xl leading-tight font-semibold tracking-tight text-white">
            Vận hành nhà máy pin, từ line tới xe xuất hàng.
          </h1>
          <ul className="mt-8 space-y-4">
            {points.map(({ icon: Icon, text }) => (
              <li key={text} className="flex items-start gap-3 text-nav-ink/90">
                <span className="mt-0.5 inline-flex size-8 shrink-0 items-center justify-center rounded-lg bg-white/5 ring-1 ring-white/10">
                  <Icon aria-hidden className="size-4 text-nav-active" />
                </span>
                {text}
              </li>
            ))}
          </ul>
        </div>
        <p className="text-xs text-nav-muted">Dự án học tập. Tên nhà máy, hệ thống và mã số là hư cấu.</p>
      </section>

      <section className="flex items-center justify-center p-6">
        <div className="w-full max-w-sm">
          <div className="mb-8 flex items-center gap-3 lg:hidden">
            <span className="inline-flex size-10 items-center justify-center rounded-lg bg-nav-active text-nav">
              <Zap aria-hidden className="size-5" strokeWidth={2.5} />
            </span>
            <span className="text-lg font-semibold">NovaVolt MOM</span>
          </div>
          <h2 className="text-2xl font-semibold tracking-tight">Đăng nhập</h2>
          <p className="mt-1 text-sm text-muted">
            Dùng tài khoản của plant <span className="font-mono">{process.env.NEXT_PUBLIC_PLANT}</span>. Bạn sẽ được chuyển
            sang trang đăng nhập Keycloak rồi quay lại đây.
          </p>
          <Button onClick={() => signIn()} busy={loading || !!session} className="mt-6 w-full">
            <LogIn aria-hidden className="size-4" />
            Đăng nhập bằng Keycloak
          </Button>
          <p className="mt-6 rounded-lg border border-line bg-raised px-3 py-2 text-xs text-muted">
            Tài khoản thử: <span className="font-mono">admin.nv1</span>, <span className="font-mono">op.nv1</span>,{" "}
            <span className="font-mono">qa.nv1</span>… Mật khẩu đặt bằng <span className="font-mono">make keycloak-users</span>.
          </p>
        </div>
      </section>
    </main>
  );
}
