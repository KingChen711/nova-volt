"use client";

import { useCallback, useEffect, useState } from "react";
import { useAuth } from "@/lib/auth";
import { postCommand, readSet, type MomResult } from "@/lib/mom";

/** Đọc một entity set; `reload` gọi lại sau khi command ghi xong. Lỗi giữ nguyên để hiện bằng ResponsePanel. */
export function useEntitySet<T>(app: string, resource: string, query: Record<string, string | undefined>) {
  const { session } = useAuth();
  const [rows, setRows] = useState<T[]>([]);
  const [error, setError] = useState<MomResult | null>(null);
  const [loading, setLoading] = useState(false);
  const key = JSON.stringify(query);

  const reload = useCallback(async () => {
    if (!session) {
      return;
    }

    setLoading(true);
    const result = await readSet<T>(session.accessToken, app, resource, JSON.parse(key) as Record<string, string>);
    setLoading(false);
    if (result.ok && result.body && typeof result.body !== "string") {
      setRows(result.body.value);
      setError(null);
    } else {
      setRows([]);
      setError(result);
    }
  }, [session, app, resource, key]);

  useEffect(() => {
    void reload();
  }, [reload]);

  return { rows, error, loading, reload };
}

/** Gửi một command; giữ kết quả thô cho ResponsePanel. */
export function useCommand(app: string, command: string) {
  const { session } = useAuth();
  const [result, setResult] = useState<MomResult | null>(null);
  const [busy, setBusy] = useState(false);

  const send = useCallback(
    async (body: unknown) => {
      if (!session) {
        return null;
      }

      setBusy(true);
      const r = await postCommand(session.accessToken, app, command, body);
      setBusy(false);
      setResult(r);
      return r;
    },
    [session, app, command],
  );

  return { send, result, busy };
}
