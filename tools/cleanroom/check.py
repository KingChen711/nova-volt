"""Kiểm clean-room (AGENTS.md K14): repo không được chứa tên, mã của dự án khách hàng.

Repo public, nên danh sách từ cấm không được nằm trong repo dưới dạng chữ. Repo chỉ giữ
HMAC-SHA256 của từng từ trong `tools/cleanroom/denylist.hmac`. Khóa HMAC là secret:
- máy cá nhân: `docs/private/cleanroom.key` (git bỏ qua);
- CI: biến môi trường `NVM_CLEANROOM_KEY` (GitHub secret).
Băm thường không đủ: từ cấm ngắn và dễ đoán, ai cũng băm thử được.

Cách dùng:
  python tools/cleanroom/check.py            # quét mọi file git đang theo dõi
  python tools/cleanroom/check.py --staged   # chỉ file đã stage (pre-commit hook)
  python tools/cleanroom/check.py --build    # dựng lại denylist.hmac từ docs/private/denylist.txt

Thoát 1 khi có vi phạm. In đường dẫn và số dòng, không in từ khớp (từ đó là bí mật).
"""

import hashlib
import hmac
import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
HASH_FILE = ROOT / "tools" / "cleanroom" / "denylist.hmac"
KEY_FILE = ROOT / "docs" / "private" / "cleanroom.key"
PLAIN_FILE = ROOT / "docs" / "private" / "denylist.txt"
MAX_BYTES = 2_000_000
# File khóa phụ thuộc: chỉ chứa tên gói và chuỗi băm ngẫu nhiên, dễ khớp nhầm từ cấm ngắn.
LOCKFILES = {"package-lock.json", "pnpm-lock.yaml", "yarn.lock", "packages.lock.json"}

TOKEN = re.compile(r"[A-Za-z0-9_]+")
# Tách camelCase, chữ hoa liền, chữ số: "AppNV1QAExt" -> App, NV, 1, QA, Ext
PART = re.compile(r"[A-Z]+(?![a-z])|[A-Z][a-z]+|[a-z]+|[0-9]+")


def load_key() -> bytes | None:
    env = os.environ.get("NVM_CLEANROOM_KEY", "").strip()
    if env:
        return env.encode()
    if KEY_FILE.exists():
        return KEY_FILE.read_text(encoding="utf-8").strip().encode()
    return None


def mac(key: bytes, word: str) -> str:
    return hmac.new(key, word.lower().encode(), hashlib.sha256).hexdigest()


def candidates(token: str):
    yield token
    pieces = [p for chunk in token.split("_") if chunk for p in PART.findall(chunk)]
    # Ghép các mảnh liền nhau, tối đa 4 mảnh, để bắt cả tên bị camelCase cắt rời.
    for i in range(len(pieces)):
        for j in range(i + 1, min(i + 4, len(pieces)) + 1):
            yield "".join(pieces[i:j])


def tracked_files(staged: bool) -> list[str]:
    if staged:
        args = ["git", "diff", "--cached", "--name-only", "--diff-filter=ACMR"]
    else:
        args = ["git", "ls-files"]
    out = subprocess.run(args, cwd=ROOT, capture_output=True, text=True, check=True).stdout
    return [line for line in out.splitlines() if line]


def build(key: bytes) -> int:
    words = []
    for line in PLAIN_FILE.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line and not line.startswith("#"):
            words.append(line.lower())
    hashes = sorted({mac(key, w) for w in words})
    HASH_FILE.write_text("\n".join(hashes) + "\n", encoding="utf-8", newline="\n")
    print(f"cleanroom: wrote {len(hashes)} entries to {HASH_FILE.relative_to(ROOT)}")
    return 0


def check(key: bytes, staged: bool) -> int:
    banned = set(HASH_FILE.read_text(encoding="utf-8").split())
    skip = {HASH_FILE.relative_to(ROOT).as_posix()}
    findings = []
    seen: dict[str, bool] = {}
    for rel in tracked_files(staged):
        if rel in skip or Path(rel).name in LOCKFILES:
            continue
        path = ROOT / rel
        if not path.is_file() or path.stat().st_size > MAX_BYTES:
            continue
        data = path.read_bytes()
        if b"\0" in data:
            continue
        text = data.decode("utf-8", errors="replace")
        for lineno, line in enumerate(text.splitlines(), 1):
            for tok in TOKEN.findall(line):
                for cand in candidates(tok):
                    low = cand.lower()
                    hit = seen.get(low)
                    if hit is None:
                        hit = mac(key, low) in banned
                        seen[low] = hit
                    if hit:
                        findings.append(f"{rel}:{lineno}")
                        break
    if findings:
        print("cleanroom: banned client terms found (K14). Locations:", file=sys.stderr)
        for f in sorted(set(findings)):
            print(f"  {f}", file=sys.stderr)
        return 1
    print("cleanroom: ok")
    return 0


def main() -> int:
    key = load_key()
    if key is None:
        if os.environ.get("NVM_CLEANROOM_REQUIRED") == "1":
            print("cleanroom: key missing (NVM_CLEANROOM_KEY).", file=sys.stderr)
            return 1
        print("cleanroom: no key on this machine, check skipped.")
        return 0
    if "--build" in sys.argv:
        return build(key)
    return check(key, staged="--staged" in sys.argv)


if __name__ == "__main__":
    sys.exit(main())
