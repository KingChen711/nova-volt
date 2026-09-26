// Không gửi command nghiệp vụ; credential chỉ ở trong bộ nhớ từ seed local đã có.
const { chromium } = require(process.argv.slice(2).find(arg => !arg.startsWith("--")) || "playwright-core");
const { readFileSync, mkdirSync } = require("node:fs");
const { resolve } = require("node:path");
const assert = require("node:assert/strict");
const { execFileSync } = require("node:child_process");

async function restoreBackend() {
    execFileSync("docker", ["start", "nvm-execution"], { stdio: "ignore", timeout: 30000 });
    const deadline = performance.now() + 60000;
    while (performance.now() < deadline) {
        try {
            const response = await fetch("http://localhost:5081/health/ready", { signal: AbortSignal.timeout(2000) });
            if (response.ok) return;
        } catch { /* Backend vẫn đang khởi động. */ }
        await new Promise(resolve => setTimeout(resolve, 500));
    }
    throw new Error("Execution did not recover within 60 seconds");
}

function isWipRead(body) {
    return body?.action === "retrieve_by_xpath" && body?.params?.xpath === "//NvmShared.WipBoard";
}

async function logoutPage(page) {
    if (page.isClosed() || !await page.evaluate(() => typeof window.mx?.logout === "function")) return;
    // Đóng browser không hủy session phía server; phải xác nhận logout trước khi bỏ cookie.
    const [response] = await Promise.all([
        page.waitForResponse(response => {
            const request = response.request();
            return new URL(response.url()).pathname === "/xas/" && request.method() === "POST"
                && request.postDataJSON()?.action === "logout";
        }, { timeout: 10000 }),
        page.evaluate(() => { window.mx.logout(); })
    ]);
    assert.equal(response.status(), 200, "Mendix logout failed; server session may remain active");
}

async function readExpectedRows(realm, user) {
    const client = realm.clients.find(candidate => candidate.clientId === "nvm-mendix");
    const tokenResponse = await fetch("http://localhost:8081/realms/novavolt/protocol/openid-connect/token", {
        method: "POST",
        body: new URLSearchParams({ grant_type: "password", client_id: client.clientId,
            client_secret: client.secret, username: user.username, password: user.credentials[0].value }),
        signal: AbortSignal.timeout(15000)
    });
    assert.equal(tokenResponse.status, 200, "Cannot authenticate the independent POM oracle");
    const token = (await tokenResponse.json()).access_token;
    const response = await fetch("http://localhost:5081/pom/v1/WipBoard?$count=true&$top=50", {
        headers: { Authorization: `Bearer ${token}` }, signal: AbortSignal.timeout(15000)
    });
    assert.equal(response.status, 200, "POM WIP read failed");
    const data = await response.json();
    assert(data["@odata.count"] > 0 && data["@odata.count"] <= 50,
        "Lab requires a nonempty fixture fitting the first WIP page (50 groups)");
    const siteId = user.username.split(".")[1].toUpperCase();
    assert(data.value.every(row => row.SiteId === siteId), "Cross-site group returned by POM");
    return data.value.map(row => [row.Line, row.StepCode, row.QualityState, String(row.UnitCount)])
        .sort((a, b) => JSON.stringify(a).localeCompare(JSON.stringify(b)));
}

async function main() {
    const slow = process.argv.includes("--slow");
    const backendDown = process.argv.includes("--backend-down");
    const failure = backendDown || process.argv.includes("--failure");
    let restoreRequired = false;
    const capture = process.argv.includes("--capture");
    const artifactDirectory = resolve(__dirname, "../../output/playwright/wip");
    if (capture) mkdirSync(artifactDirectory, { recursive: true });
    const realm = JSON.parse(readFileSync(resolve(__dirname, "../../deploy/keycloak/realm-novavolt.json"), "utf8"));
    const browser = await chromium.launch({ channel: "chrome", headless: true });
    const pagesToLogout = new Set();
    let currentPage;
    try {
        for (const username of slow || failure ? ["op.nv1"] : ["op.nv1", "op.de1"]) {
            const user = realm.users.find(candidate => candidate.username === username);
            const expectedRows = await readExpectedRows(realm, user);
            const context = await browser.newContext();
            const page = await context.newPage();
            pagesToLogout.add(page);
            currentPage = page;
            await page.goto("http://localhost:8080/oauth/v2/login");
            await page.getByRole("textbox", { name: "Username or email" }).fill(username);
            await page.getByRole("textbox", { name: "Password", exact: true }).fill(user.credentials[0].value);
            await page.getByRole("button", { name: "Sign In", exact: true }).click();
            await page.waitForFunction(() => document.title.endsWith("Dispatch List"));
            await page.getByRole("menuitem", { name: "WIP", exact: true }).last().click();
            await page.waitForFunction(() => document.title.endsWith("WIP board"));
            await page.getByRole("cell", { name: "EOL", exact: true }).waitFor();
            const rows = [];
            for (const row of await page.getByRole("row").all()) {
                const cells = await row.getByRole("cell").allTextContents();
                if (cells.length) {
                    assert.equal(cells.length, 4, "Unexpected WIP columns");
                    rows.push(cells.map(cell => cell.trim()));
                }
            }
            rows.sort((a, b) => JSON.stringify(a).localeCompare(JSON.stringify(b)));
            assert.deepEqual(rows, expectedRows, "Browser WIP differs from authenticated POM");
            await page.getByRole("status").filter({ hasText: /^Connected$/ }).waitFor();
            const firstSuccess = await page.locator("time").getAttribute("datetime");
            assert(firstSuccess, "Last successful read missing");
            if (capture) await page.screenshot({ path: resolve(artifactDirectory, `${username}-connected.png`), fullPage: true });
            if (failure) {
                let failReads = true;
                if (backendDown) {
                    assert.equal(execFileSync("docker", ["inspect", "nvm-execution", "--format", "{{.State.Status}}"],
                        { encoding: "utf8", timeout: 10000 }).trim(), "running", "Backend must be running before this lab");
                    restoreRequired = true;
                    execFileSync("docker", ["stop", "nvm-execution"], { stdio: "ignore", timeout: 30000 });
                } else {
                    await page.route("**/xas/", route => isWipRead(route.request().postDataJSON()) && failReads
                        ? route.abort("failed") : route.continue());
                }
                await page.getByRole("button", { name: "Refresh now", exact: true }).click();
                await page.getByRole("status").filter({ hasText: "Disconnected" }).waitFor();
                assert.equal(await page.locator("time").getAttribute("datetime"), firstSuccess);
                const keptRows = await page.getByRole("cell").allTextContents();
                assert.deepEqual(keptRows.sort(), rows.flat().sort(), "Failure changed visible WIP snapshot");
                if (capture) await page.screenshot({ path: resolve(artifactDirectory, `${username}-disconnected.png`), fullPage: true });
                failReads = false;
                if (backendDown) { await restoreBackend(); restoreRequired = false; }
                await page.getByRole("button", { name: "Retry now", exact: true }).click();
                await page.getByRole("status").filter({ hasText: /^Connected$/ }).waitFor();
                assert.notEqual(await page.locator("time").getAttribute("datetime"), firstSuccess);
                console.log(JSON.stringify({ username, failureRecovery: "passed", backendDown, preservedRows: rows.length }));
                await logoutPage(page); pagesToLogout.delete(page); await context.close();
                continue;
            }
            if (slow) {
                // Chỉ giữ phản hồi trong browser riêng; không đổi backend hay cấu hình app.
                await page.route("**/xas/", async route => {
                    const body = route.request().postDataJSON();
                    if (!isWipRead(body)) return route.continue();
                    const response = await route.fetch();
                    await new Promise(resolve => setTimeout(resolve, 7500));
                    await route.fulfill({ response });
                });
            }
            const calls = [];
            const pending = new Map();
            let maxConcurrent = 0;
            const started = performance.now();
            page.on("request", request => {
                if (request.method() !== "POST" || new URL(request.url()).pathname !== "/xas/") return;
                let body;
                try { body = request.postDataJSON(); } catch { return; }
                if (!isWipRead(body)) return;
                const call = {
                    path: new URL(request.url()).pathname,
                    action: body?.action,
                    startMs: performance.now() - started,
                    endMs: null,
                    httpStatus: null,
                    transportFailed: false
                };
                calls.push(call);
                pending.set(request, call);
                maxConcurrent = Math.max(maxConcurrent, pending.size);
            });
            const finish = request => {
                const call = pending.get(request);
                if (!call) return;
                call.endMs = performance.now() - started;
                call.transportFailed = request.failure() !== null;
                pending.delete(request);
            };
            page.on("response", response => {
                const call = pending.get(response.request());
                if (call) call.httpStatus = response.status();
            });
            page.on("requestfinished", finish);
            page.on("requestfailed", finish);
            const minimumCalls = slow ? 2 : 3;
            const deadline = performance.now() + 45000;
            while (calls.filter(call => call.endMs !== null).length < minimumCalls && performance.now() < deadline) {
                await page.waitForTimeout(100);
            }
            const report = { username, slow, rows, calls, maxConcurrent, unfinished: pending.size };
            console.log(JSON.stringify(report));
            assert(calls.filter(call => call.endMs !== null).length >= minimumCalls, "Too few completed automatic reloads");
            assert.equal(maxConcurrent, 1, "Overlapping automatic reloads");
            assert.equal(pending.size, 0, "Reload still in flight at observation boundary");
            assert(!calls.some(call => call.transportFailed), "Transport failure during reload");
            assert(calls.every(call => call.httpStatus === 200), "HTTP error during reload");
            if (slow) assert(calls.every(call => call.endMs - call.startMs >= 7500), "Delay not exercised");
            assert.notEqual(await page.locator("time").getAttribute("datetime"), firstSuccess);
            assert.deepEqual(await readExpectedRows(realm, user), expectedRows, "Fixture changed during observation");
            await logoutPage(page);
            pagesToLogout.delete(page);
            await context.close();
        }
    } catch (error) {
        if (currentPage && !currentPage.isClosed()) {
            console.error(JSON.stringify({ title: await currentPage.title(), path: new URL(currentPage.url()).pathname,
                pageText: (await currentPage.locator("body").innerText()).slice(0, 2000) }));
        }
        throw error;
    } finally {
        if (restoreRequired) {
            try { await restoreBackend(); }
            catch { console.error("Execution recovery failed; inspect nvm-execution."); process.exitCode = 1; }
        }
        for (const page of pagesToLogout) {
            try { await logoutPage(page); }
            catch {
                console.error("Mendix logout cleanup failed; server session may remain active.");
                process.exitCode = 1;
            }
        }
        await browser.close();
    }
}

main().catch(error => {
    console.error(error.name + ": " + error.message);
    process.exitCode = 1;
});
