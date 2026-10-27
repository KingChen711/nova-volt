"use client";

import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { useAuth } from "@/lib/auth";

/** Keycloak trả về đây sau khi đăng nhập (authorization code + PKCE). */
export default function Callback() {
  const { completeSignIn } = useAuth();
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const started = useRef(false);

  useEffect(() => {
    // StrictMode chạy effect hai lần ở dev; mã đăng nhập chỉ đổi được một lần.
    if (started.current) {
      return;
    }

    started.current = true;
    completeSignIn()
      .then(() => router.replace("/"))
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)));
  }, [completeSignIn, router]);

  return (
    <main className="p-6">
      {error ? (
        <p role="alert" className="text-bad">
          Đăng nhập không thành công: {error}
        </p>
      ) : (
        <p className="text-muted">Đang hoàn tất đăng nhập…</p>
      )}
    </main>
  );
}
