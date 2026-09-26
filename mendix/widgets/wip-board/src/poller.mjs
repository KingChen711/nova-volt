export const PAGE_SIZE = 50;
export const REFRESH_MS = 5000;

export function initialWipState() {
    return {
        rows: [], offset: 0, hasNext: false, hasLoaded: false,
        status: "loading", isFetching: false, lastSuccess: null
    };
}

/** Đợi request kết thúc rồi mới đặt timer; không chồng lượt tự tải hoặc bấm tay. */
export function createWipPoller({
    retrieve, onChange, now = () => new Date(),
    setTimer = setTimeout, clearTimer = clearTimeout
}) {
    let state = initialWipState();
    let timer;
    let disposed = false;
    let started = false;
    let inFlight = false;

    function emit(update) {
        state = { ...state, ...update };
        onChange(state);
    }

    async function fetchPage(offset) {
        if (disposed || inFlight) return false;
        inFlight = true;
        clearTimer(timer);
        timer = undefined;
        emit({ isFetching: true });
        try {
            const rows = await retrieve(offset, PAGE_SIZE);
            if (disposed) return false;
            emit({
                rows, offset, hasNext: rows.length === PAGE_SIZE,
                hasLoaded: true, status: "connected", lastSuccess: now()
            });
        } catch {
            // Giữ nguyên ảnh chụp và trang thành công gần nhất khi lần đọc mới lỗi.
            if (!disposed) emit({ status: "disconnected" });
        } finally {
            inFlight = false;
            if (!disposed) {
                emit({ isFetching: false });
                timer = setTimer(() => { void fetchPage(state.offset); }, REFRESH_MS);
            }
        }
        return true;
    }

    return {
        start() {
            if (started || disposed) return;
            started = true;
            void fetchPage(0);
        },
        refresh: () => fetchPage(state.offset),
        next: () => state.hasNext ? fetchPage(state.offset + PAGE_SIZE) : Promise.resolve(false),
        previous: () => state.offset > 0 ? fetchPage(Math.max(0, state.offset - PAGE_SIZE)) : Promise.resolve(false),
        dispose() {
            disposed = true;
            clearTimer(timer);
            timer = undefined;
        }
    };
}
