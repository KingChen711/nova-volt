import { WipBoardPreviewProps } from "../typings/WipBoardProps";

export function preview(props: WipBoardPreviewProps) {
    return <div><strong>{props.tableCaption}</strong><p>Connected · Last successful read: runtime timestamp</p>
        <table><thead><tr><th>Line</th><th>Step</th><th>Quality state</th><th>Units</th></tr></thead>
            <tbody><tr><td>L1</td><td>STACK</td><td>Pending</td><td>2</td></tr></tbody></table>
        <p>Refreshes 5 seconds after the previous read completes · 50 groups per page</p></div>;
}

export function getPreviewCss() { return ""; }
