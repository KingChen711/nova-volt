"use client";

import { useState } from "react";
import { AreaTabs } from "@/components/AreaTabs";
import { Badge, Button, DataTable, Field, PageHeader, Panel, RefreshButton, ResponsePanel, Toast, useToast } from "@/components/ui";
import { useAuth } from "@/lib/auth";
import { useEntitySet } from "@/lib/hooks";
import { postCommand, type MomResult } from "@/lib/mom";

type Trailer = { NId: string; TrailerNo: string | null; Status: string | null; ContentType: string | null; LocationNId: string | null; Route: string | null; PlacardNId: string | null; YardPassNId: string | null; LastComment: string | null };
type Movement = { NId: string; TrailerNId: string; FromNId: string | null; ToNId: string; Status: string };

const app = "YardApp";
const next: Record<string, string> = { Created: "Accepted", Accepted: "Picked", Picked: "Dropped" };

/** Bàn làm việc ở dock và của người kéo xe: gọi xe, báo chất hàng trả, yard pass, cho xe rời; làm từng bước của yêu cầu kéo. */
export default function DockPage() {
  const { session } = useAuth();
  const toast = useToast();
  const trailers = useEntitySet<Trailer>(app, "Trailer", { $filter: "LocationNId ne null", $orderby: "LocationNId", $top: "100" });
  const movements = useEntitySet<Movement>(app, "MyTrailerMovements()", { $orderby: "CreatedOn" });
  const [picked, setPicked] = useState("");
  const [destination, setDestination] = useState("");
  const [route, setRoute] = useState("");
  const [placard, setPlacard] = useState("");
  const [percent, setPercent] = useState("100");
  const [pass, setPass] = useState("");
  const [result, setResult] = useState<MomResult | null>(null);
  const [busy, setBusy] = useState(false);
  const trailer = trailers.rows.find((t) => t.NId === picked);

  async function run(command: string, body: Record<string, unknown>, done: string) {
    if (!session) {
      return;
    }

    setBusy(true);
    const r = await postCommand(session.accessToken, app, command, body);
    setBusy(false);
    setResult(r);
    if (r.ok) {
      toast.show(done);
      await Promise.all([trailers.reload(), movements.reload()]);
    }
  }

  return (
    <>
      <PageHeader title="Điều xe" description="Gọi xe vào dock, chất hàng trả, yard pass, xe rời; yêu cầu kéo xe">
        <RefreshButton onClick={() => void Promise.all([trailers.reload(), movements.reload()])} busy={trailers.loading} />
      </PageHeader>
      <AreaTabs slug="yard" label="Màn cổng và dock" />
      <Toast message={toast.message} onDone={toast.clear} />

      <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_26rem]">
        <div className="flex flex-col gap-4">
          <Panel flush title={<>Xe trong nhà máy <span className="font-normal text-muted">({trailers.rows.length})</span></>}>
            {trailers.error ? (
              <ResponsePanel result={trailers.error} />
            ) : (
              <DataTable
                rows={trailers.rows}
                loading={trailers.loading}
                empty="Không có xe nào trong nhà máy"
                columns={[
                  {
                    key: "barcode",
                    label: "Barcode",
                    mono: true,
                    render: (t) => (
                      <button type="button" onClick={() => setPicked(t.NId)} className="cursor-pointer text-primary underline underline-offset-2 hover:text-primary-hover">
                        {t.NId}
                      </button>
                    ),
                  },
                  { key: "status", label: "Trạng thái", render: (t) => <Badge>{t.Status}</Badge> },
                  { key: "content", label: "Nội dung", render: (t) => t.ContentType ?? "—" },
                  { key: "location", label: "Vị trí", mono: true, render: (t) => t.LocationNId },
                  { key: "route", label: "Route", mono: true, render: (t) => t.Route ?? "—" },
                  { key: "placard", label: "Placard", mono: true, render: (t) => t.PlacardNId ?? "—" },
                ]}
              />
            )}
          </Panel>

          <Panel flush title={<>Yêu cầu kéo xe <span className="font-normal text-muted">({movements.rows.length})</span></>}>
            <DataTable
              rows={movements.rows}
              loading={movements.loading}
              empty="Không có yêu cầu đang mở trong khu của bạn"
              columns={[
                { key: "trailer", label: "Trailer", mono: true, render: (m) => m.TrailerNId },
                { key: "route", label: "Từ → tới", mono: true, render: (m) => `${m.FromNId ?? "—"} → ${m.ToNId}` },
                { key: "status", label: "Bước", render: (m) => <Badge>{m.Status}</Badge> },
                {
                  key: "action",
                  label: "",
                  render: (m) => (
                    <Button variant="secondary" busy={busy} onClick={() => void run("UpdateTrailerMovement", { MovementNId: m.NId, Status: next[m.Status] }, `Yêu cầu sang ${next[m.Status]}`)}>
                      {next[m.Status]}
                    </Button>
                  ),
                },
              ]}
            />
          </Panel>
          <ResponsePanel result={result} />
        </div>

        <Panel title={trailer ? `Trailer ${trailer.NId}` : "Chọn một trailer"}>
          {!trailer ? (
            <p className="text-muted">Bấm barcode trong danh sách để thao tác.</p>
          ) : (
            <div className="flex flex-col gap-4">
              {trailer.LastComment && <p className="text-muted">Ghi chú: {trailer.LastComment}</p>}
              <div className="flex flex-col gap-3">
                <Field id="dk-destination" label="Vị trí đích" required mono value={destination} onChange={setDestination} hint="Cửa dock hoặc ô yard" />
                <div className="flex flex-wrap gap-2">
                  <Button variant="secondary" busy={busy} disabled={!destination} onClick={() => void run("CreateTrailerMovementRequest", { Barcode: trailer.NId, DestinationNId: destination }, "Đã tạo yêu cầu kéo xe")}>
                    Tạo yêu cầu kéo
                  </Button>
                  <Button variant="secondary" busy={busy} disabled={!destination} onClick={() => void run("CallEmptyTrailer", { Barcode: trailer.NId, DockNId: destination }, "Đã gọi xe rỗng")}>
                    Gọi xe rỗng
                  </Button>
                </div>
              </div>
              <div className="flex flex-col gap-3 border-t border-line-soft pt-4">
                <Field id="dk-route" label="Route trả" mono value={route} onChange={setRoute} />
                <Field id="dk-placard" label="Placard trả hàng" mono value={placard} onChange={setPlacard} />
                <Field id="dk-percent" label="Phần trăm đã chất" mono value={percent} onChange={setPercent} />
                <div className="flex flex-wrap gap-2">
                  <Button variant="secondary" busy={busy} disabled={!route} onClick={() => void run("GeneratePlacard", { Type: "ReturnComponent", Route: route }, "Đã sinh placard trả hàng")}>
                    Sinh placard
                  </Button>
                  <Button
                    busy={busy}
                    disabled={!route || !placard || !destination}
                    onClick={() => void run("ReportTrailerReloaded", { Barcode: trailer.NId, ReturnRoute: route, PlacardNId: placard, Percent: Number(percent), DestinationNId: destination }, "Đã báo chất xong")}
                  >
                    Báo đã chất
                  </Button>
                </div>
              </div>
              <div className="flex flex-col gap-3 border-t border-line-soft pt-4">
                <Field id="dk-pass" label="Yard pass" mono value={pass} onChange={setPass} hint={trailer.YardPassNId ? `Yard pass của xe: ${trailer.YardPassNId}` : "Xe của hãng vận chuyển rời đi không cần"} />
                <div className="flex flex-wrap gap-2">
                  <Button variant="secondary" busy={busy} onClick={() => void run("IssueTrailerYardPass", { Barcode: trailer.NId }, "Đã in yard pass")}>
                    In yard pass
                  </Button>
                  <Button busy={busy} onClick={() => void run("DepartTrailer", { Barcode: trailer.NId, YardPassNId: pass || undefined }, "Xe đã rời nhà máy")}>
                    Cho xe rời
                  </Button>
                </div>
              </div>
            </div>
          )}
        </Panel>
      </div>
    </>
  );
}
