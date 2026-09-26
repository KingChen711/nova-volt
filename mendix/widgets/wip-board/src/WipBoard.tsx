import { useEffect, useRef, useState } from "react";
import { retrieveByEntity } from "mx-api/data";
import { createWipPoller, initialWipState, PAGE_SIZE } from "./poller.mjs";
import { WipBoardContainerProps } from "../typings/WipBoardProps";
import "./ui/WipBoard.css";

type Row = { key: string; line: string; step: string; quality: string; count: string };

async function retrieveWipPage(offset: number, limit: number): Promise<Row[]> {
    const objects = await retrieveByEntity({
        entity: "NvmShared.WipBoard",
        filter: { offset, limit, sort: [["Line", "asc"], ["StepCode", "asc"], ["QualityState", "asc"]] }
    });
    // MxObject dùng cache chung; chép giá trị ngay để lần đọc lỗi không đổi ảnh chụp cũ.
    return objects.map(object => ({
        key: object.getGuid(),
        line: String(object.get("Line") ?? ""),
        step: String(object.get("StepCode") ?? ""),
        quality: String(object.get("QualityState") ?? ""),
        count: String(object.get("UnitCount") ?? "")
    }));
}

export function WipBoard(props: WipBoardContainerProps) {
    const [state, setState] = useState(initialWipState);
    const poller = useRef<ReturnType<typeof createWipPoller> | null>(null);
    useEffect(() => {
        const active = createWipPoller({ retrieve: retrieveWipPage, onChange: setState });
        poller.current = active;
        active.start();
        return () => {
            active.dispose();
            poller.current = null;
        };
    }, []);

    const lastSuccess = state.lastSuccess as Date | null;
    const rows = state.rows as Row[];
    const statusText = state.status === "disconnected"
        ? "Disconnected — the latest WIP read failed. Retrying automatically."
        : state.status === "loading"
            ? "Loading WIP…"
            : "Connected";

    return <section className={`nvm-wip-board ${props.class ?? ""}`} style={props.style}
        aria-label={props.tableCaption} tabIndex={props.tabIndex}>
        <div className="nvm-wip-toolbar">
            <div>
                <p className={`nvm-wip-status nvm-wip-status-${state.status}`} role="status" aria-live="polite">
                    {statusText}
                </p>
                <p className="nvm-wip-updated">
                    Last successful read: {lastSuccess
                        ? <time dateTime={lastSuccess.toISOString()}>{lastSuccess.toLocaleString()}</time>
                        : "Never"}
                    {state.isFetching && state.hasLoaded ? " · Refreshing…" : ""}
                </p>
            </div>
            <button type="button" className="btn btn-default" disabled={state.isFetching}
                onClick={() => { void poller.current?.refresh(); }}>
                {state.status === "disconnected" ? "Retry now" : "Refresh now"}
            </button>
        </div>
        <div className="nvm-wip-table-container" aria-busy={state.isFetching}>
            <table className="nvm-wip-table">
                <caption>{props.tableCaption}</caption>
                <thead><tr><th scope="col">Line</th><th scope="col">Step</th>
                    <th scope="col">Quality state</th><th scope="col" className="nvm-wip-count">Units</th></tr></thead>
                <tbody>{rows.map(row => <tr key={row.key}>
                    <td>{row.line}</td><td>{row.step}</td><td>{row.quality}</td>
                    <td className="nvm-wip-count">{row.count}</td>
                </tr>)}</tbody>
            </table>
            {rows.length === 0 && <p className="nvm-wip-empty">{state.hasLoaded
                ? "No WIP groups on this page."
                : state.status === "disconnected" ? "WIP data is unavailable." : "Waiting for the first WIP read…"}</p>}
        </div>
        <nav className="nvm-wip-pagination" aria-label="WIP pages">
            <button type="button" className="btn btn-default" disabled={state.isFetching || state.offset === 0}
                onClick={() => { void poller.current?.previous(); }}>Previous</button>
            <span>Page {Math.floor(state.offset / PAGE_SIZE) + 1} · {rows.length} groups
                {state.hasNext ? " · More groups may follow" : ""}</span>
            <button type="button" className="btn btn-default" disabled={state.isFetching || !state.hasNext}
                onClick={() => { void poller.current?.next(); }}>Next</button>
        </nav>
    </section>;
}
