// Client của service layer MOM: chỉ OData và command API (AGENTS.md K10). Lỗi trả nguyên thân để màn hiện đủ mã và câu lỗi.

const base = `${process.env.NEXT_PUBLIC_MOM_URL}/mom/${process.env.NEXT_PUBLIC_PLANT}/Application`;

/** Kết quả thô của một lời gọi: mã HTTP, thân JSON (hoặc văn bản nếu không phải JSON), CorrelationId để tra trace. */
export type MomResult<T = unknown> = {
  ok: boolean;
  status: number;
  body: T | string | null;
  correlationId: string | null;
};

export type ODataList<T> = { "@odata.count"?: number; value: T[] };

async function call<T>(token: string, method: "GET" | "POST", url: string, body?: unknown): Promise<MomResult<T>> {
  const response = await fetch(url, {
    method,
    headers: { Authorization: `Bearer ${token}`, ...(body === undefined ? {} : { "Content-Type": "application/json" }) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  let parsed: T | string | null = text || null;
  try {
    parsed = text ? (JSON.parse(text) as T) : null;
  } catch {
    // Giữ nguyên văn bản: thân bị cắt (HTTP 200 mà JSON dở) cũng là thông tin chẩn đoán.
  }

  return { ok: response.ok, status: response.status, body: parsed, correlationId: response.headers.get("X-Correlation-Id") };
}

/** Đọc entity set hoặc reading function của app, với tham số OData ($filter, $orderby, $top, $count…). */
export function readSet<T>(token: string, app: string, resource: string, query: Record<string, string | undefined> = {}) {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value) {
      params.set(key, value);
    }
  }

  const suffix = params.size > 0 ? `?${params.toString()}` : "";
  return call<ODataList<T>>(token, "GET", `${base}/${app}/odata/${resource}${suffix}`);
}

/** POST command theo tên công bố trên app (vd "SetConfigurationKey", "LabExt_TagLabOrder"). */
export function postCommand(token: string, app: string, command: string, body: unknown) {
  return call<Record<string, unknown>>(token, "POST", `${base}/${app}/odata/${command}`, body);
}

/** Chuỗi OData cho một giá trị chuỗi: bọc nháy đơn, nhân đôi nháy bên trong. */
export function quote(value: string): string {
  return `'${value.replace(/'/g, "''")}'`;
}
