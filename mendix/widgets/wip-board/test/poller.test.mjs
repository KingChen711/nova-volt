import { test } from "node:test";
import assert from "node:assert/strict";
import { createWipPoller, PAGE_SIZE, REFRESH_MS } from "../src/poller.mjs";

const flush = () => new Promise(resolve => setImmediate(resolve));
function fixture() {
    const calls = [], states = [], timers = new Map();
    let id = 0, time = 0;
    const poller = createWipPoller({
        retrieve: (offset, limit) => new Promise((resolve, reject) => calls.push({ offset, limit, resolve, reject })),
        onChange: state => states.push(state), now: () => ++time,
        setTimer: (callback, delay) => { timers.set(++id, { callback, delay }); return id; },
        clearTimer: key => timers.delete(key)
    });
    return { poller, calls, states, timers, get state() { return states.at(-1); },
        tick() { const [key, timer] = timers.entries().next().value; timers.delete(key); timer.callback(); } };
}

test("slow reads and repeated manual refresh cannot overlap; delay starts after completion", async () => {
    const f = fixture();
    f.poller.start(); f.poller.start();
    assert.equal(await f.poller.refresh(), false);
    assert.equal(f.calls.length, 1);
    assert.equal(f.timers.size, 0);
    f.calls[0].resolve([{ key: "a" }]); await flush();
    assert.equal(f.state.lastSuccess, 1);
    assert.equal(f.timers.size, 1);
    assert.equal([...f.timers.values()][0].delay, REFRESH_MS);
    f.tick();
    assert.equal(f.calls.length, 2);
    assert.equal(f.timers.size, 0);
    assert.equal(await f.poller.refresh(), false);
    f.poller.dispose(); f.calls[1].resolve([]); await flush();
});

test("failed refresh preserves rows/time and retries the same page, recovery updates both", async () => {
    const f = fixture(); f.poller.start();
    const original = [{ key: "a" }];
    f.calls[0].resolve(original); await flush(); f.tick();
    f.calls[1].reject(new Error("offline")); await flush();
    assert.equal(f.state.status, "disconnected");
    assert.equal(f.state.rows, original);
    assert.equal(f.state.lastSuccess, 1);
    assert.equal(f.state.isFetching, false);
    f.tick(); f.calls[2].resolve([{ key: "b" }]); await flush();
    assert.equal(f.state.status, "connected");
    assert.equal(f.state.lastSuccess, 2);
    assert.deepEqual(f.state.rows, [{ key: "b" }]);
    f.poller.dispose();
});

test("first read failure does not claim an empty successful result", async () => {
    const f = fixture(); f.poller.start();
    f.calls[0].reject(new Error("denied")); await flush();
    assert.equal(f.state.hasLoaded, false);
    assert.equal(f.state.lastSuccess, null);
    assert.equal(f.state.status, "disconnected");
    f.poller.dispose(); assert.equal(f.timers.size, 0);
});

test("failed navigation keeps previous page; next and previous use bounded reads", async () => {
    const f = fixture(); f.poller.start();
    f.calls[0].resolve(Array.from({ length: PAGE_SIZE }, (_, key) => ({ key }))); await flush();
    const next = f.poller.next();
    assert.equal(f.calls[1].offset, PAGE_SIZE);
    assert.equal(f.calls[1].limit, PAGE_SIZE);
    f.calls[1].reject(new Error("offline")); await next;
    assert.equal(f.state.offset, 0);
    assert.equal(f.state.rows.length, PAGE_SIZE);
    const retry = f.poller.next(); f.calls[2].resolve([{ key: "last" }]); await retry;
    assert.equal(f.state.offset, PAGE_SIZE);
    assert.equal(f.state.hasNext, false);
    assert.equal(await f.poller.next(), false);
    const previous = f.poller.previous();
    assert.equal(f.calls[3].offset, 0);
    f.calls[3].resolve([]); await previous;
    assert.equal(f.state.offset, 0);
    assert.equal(f.state.hasLoaded, true);
    f.poller.dispose();
});

for (const outcome of ["resolve", "reject"]) {
    test(`dispose prevents late ${outcome} updating state or scheduling another read`, async () => {
        const f = fixture(); f.poller.start(); f.poller.dispose();
        const count = f.states.length;
        f.calls[0][outcome](outcome === "resolve" ? [] : new Error("late")); await flush();
        assert.equal(f.states.length, count);
        assert.equal(f.timers.size, 0);
        assert.equal(await f.poller.refresh(), false);
        f.poller.start(); assert.equal(f.calls.length, 1);
    });
}
