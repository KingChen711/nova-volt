"use client";

import { useState } from "react";
import { AreaTabs } from "@/components/AreaTabs";
import { Badge, Button, DataTable, Field, PageHeader, Panel, RefreshButton, ResponsePanel, Toast, useToast } from "@/components/ui";
import { useAuth } from "@/lib/auth";
import { useEntitySet } from "@/lib/hooks";
import { postCommand, quote, type MomResult } from "@/lib/mom";

type Schedule = { NId: string; Route: string | null; TrailerNo: string | null; GateStatus: string | null; PlannedSkids: number; PlannedOn: string | null };
type Location = { NId: string };

const app = "YardApp";
const types = [
  { value: "DirectMaterial", label: "Nguyên liệu đặt thẳng" },
  { value: "PipedDirectMaterial", label: "Nguyên liệu đặt thẳng, loại ống" },
  { value: "AdHocComponents", label: "Linh kiện đột xuất" },
  { value: "CustomerEmpty", label: "Xe rỗng của khách" },
  { value: "ReturnedGoods", label: "Thành phẩm trả về" },
];

/** Việc ở cổng: khai trailer, đăng ký xe linh kiện theo lịch, xe rỗng, xe không lịch. */
export default function InboundPage() {
  const { session } = useAuth();
  const toast = useToast();
  const schedules = useEntitySet<Schedule>(app, "ArrivalSchedules()", { $orderby: "PlannedOn" });
  const [picked, setPicked] = useState<Schedule | null>(null);
  const [barcode, setBarcode] = useState("");
  const [trailerNo, setTrailerNo] = useState("");
  const [yard, setYard] = useState("NV1-CMN-YARD-Y01");
  const [type, setType] = useState("DirectMaterial");
  const [plate, setPlate] = useState("");
  const [carrier, setCarrier] = useState("");
  const [door, setDoor] = useState("");
  const [route, setRoute] = useState("");
  const [placard, setPlacard] = useState("");
  const locations = useEntitySet<Location>(app, `UnplannedLocations(TrailerType=${quote(type)})`, { $orderby: "NId" });
  const [result, setResult] = useState<MomResult | null>(null);
  const [busy, setBusy] = useState(false);

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
      await Promise.all([schedules.reload(), locations.reload()]);
    }
  }

  const needsCarrier = type === "DirectMaterial" || type === "PipedDirectMaterial" || type === "AdHocComponents";
  return (
    <>
      <PageHeader title="Xe đến" description="Đăng ký xe ở cổng: xe linh kiện theo lịch, xe rỗng, xe không lịch">
        <RefreshButton onClick={() => void schedules.reload()} busy={schedules.loading} />
      </PageHeader>
      <AreaTabs slug="yard" label="Màn cổng và dock" />
      <Toast message={toast.message} onDone={toast.clear} />

      <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_26rem]">
        <div className="flex flex-col gap-4">
          <Panel flush title={<>Lịch xe chưa tới <span className="font-normal text-muted">({schedules.rows.length})</span></>}>
            {schedules.error ? (
              <ResponsePanel result={schedules.error} />
            ) : (
              <DataTable
                rows={schedules.rows}
                loading={schedules.loading}
                empty="Chưa có lịch xe từ cổng nhà cung cấp"
                columns={[
                  {
                    key: "route",
                    label: "Route",
                    mono: true,
                    render: (s) => (
                      <button type="button" onClick={() => setPicked(s)} className="cursor-pointer text-primary underline underline-offset-2 hover:text-primary-hover">
                        {s.Route}
                      </button>
                    ),
                  },
                  { key: "trailer", label: "Số xe", mono: true, render: (s) => s.TrailerNo ?? "—" },
                  { key: "gate", label: "Trạng thái", render: (s) => <Badge>{s.GateStatus}</Badge> },
                  { key: "skids", label: "Skid dự kiến", render: (s) => s.PlannedSkids },
                  { key: "planned", label: "Giờ dự kiến (UTC)", mono: true, render: (s) => s.PlannedOn?.slice(0, 16).replace("T", " ") ?? "—" },
                ]}
              />
            )}
          </Panel>
          <ResponsePanel result={result} />
        </div>

        <div className="flex flex-col gap-4">
          <Panel title={picked ? `Đăng ký xe route ${picked.Route}` : "Trailer và đăng ký"}>
            <div className="flex flex-col gap-3">
              <Field id="ib-barcode" label="Barcode trailer" required mono value={barcode} onChange={setBarcode} />
              <Field id="ib-no" label="Số xe" mono value={trailerNo} onChange={setTrailerNo} hint="Chỉ cần khi khai trailer mới" />
              <Field id="ib-yard" label="Ô yard" required mono value={yard} onChange={setYard} />
              <div className="flex flex-wrap gap-2">
                <Button variant="secondary" busy={busy} disabled={!barcode} onClick={() => void run("CreateTrailer", { Barcode: barcode, TrailerNo: trailerNo || undefined }, "Đã khai trailer")}>
                  Khai trailer
                </Button>
                <Button variant="secondary" busy={busy} disabled={!barcode} onClick={() => void run("ArriveEmptyTrailer", { Barcode: barcode, LocationNId: yard }, "Xe rỗng đã vào bãi")}>
                  Đăng ký xe rỗng
                </Button>
                <Button
                  busy={busy}
                  disabled={!barcode || !picked}
                  onClick={() => void run("ArriveComponentTrailer", { ScheduleNId: picked?.NId, Barcode: barcode, LocationNId: yard }, "Xe đã vào bãi")}
                >
                  Đăng ký xe linh kiện
                </Button>
              </div>
            </div>
          </Panel>

          <Panel title="Xe không lịch">
            <div className="flex flex-col gap-3">
              <div className="flex flex-col gap-1">
                <label htmlFor="ib-type" className="font-bold">Loại xe</label>
                <select id="ib-type" value={type} onChange={(e) => setType(e.target.value)} className="min-h-touch rounded-lg border border-field bg-surface px-3">
                  {types.map((t) => (
                    <option key={t.value} value={t.value}>{t.label}</option>
                  ))}
                </select>
              </div>
              <Field id="ib-plate" label={needsCarrier ? "Biển số" : "Barcode trailer đã khai"} required mono value={plate} onChange={setPlate} />
              {needsCarrier && <Field id="ib-carrier" label="Hãng xe" required value={carrier} onChange={setCarrier} />}
              {type !== "PipedDirectMaterial" && (
                <div className="flex flex-col gap-1">
                  <label htmlFor="ib-door" className="font-bold">Vị trí còn trống</label>
                  <select id="ib-door" value={door} onChange={(e) => setDoor(e.target.value)} className="min-h-touch rounded-lg border border-field bg-surface px-3 font-mono">
                    <option value="">Chọn vị trí</option>
                    {locations.rows.map((l) => (
                      <option key={l.NId} value={l.NId}>{l.NId}</option>
                    ))}
                  </select>
                </div>
              )}
              {type === "ReturnedGoods" && <Field id="ib-route" label="Route" required mono value={route} onChange={setRoute} />}
              {!needsCarrier && <Field id="ib-placard" label="Placard" required mono value={placard} onChange={setPlacard} />}
              <div className="flex flex-wrap gap-2">
                {!needsCarrier && (
                  <Button
                    variant="secondary"
                    busy={busy}
                    onClick={() => void run("GeneratePlacard", { Type: type === "CustomerEmpty" ? "Empty" : "ReturnGoods", Route: route || undefined }, "Đã sinh placard")}
                  >
                    Sinh placard
                  </Button>
                )}
                <Button
                  busy={busy}
                  disabled={!plate}
                  onClick={() =>
                    void run("RegisterUnplannedTrailer", { TrailerType: type, Trailer: plate, Carrier: carrier || undefined, LocationNId: door || undefined, Route: route || undefined, PlacardNId: placard || undefined }, "Đã đăng ký xe không lịch")
                  }
                >
                  Đăng ký
                </Button>
              </div>
            </div>
          </Panel>
        </div>
      </div>
    </>
  );
}
