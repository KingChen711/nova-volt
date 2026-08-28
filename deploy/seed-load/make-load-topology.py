#!/usr/bin/env python3
"""Sinh factory model của bài test/load 1.000 kênh formation.

VÌ SAO CÓ FILE NÀY, thay vì gõ tay 1.000 dòng vào `deploy/seed/`:

  Seed demo phải ở lại **8 kênh** (scope.md §9/M2, quyết định G1). Nó là thứ người ta đọc để
  hiểu cây nhà máy, và một file 1.000 dòng thì không ai đọc.

  Nhưng cardinality của alias table và session là phần THẬT của ingestion, không phải của
  simulator: một edge node khai 1.000 device nghĩa là 1.000 bảng alias sống cùng lúc dưới một
  `bdSeq`, và mất một DBIRTH trong số đó là mất khả năng đọc mọi DDATA của đúng kênh ấy cho tới
  hết phiên. Không có topology 1.000 kênh thì mệnh đề đó chưa từng được chạy.

  Nên: một catalog RIÊNG, cùng hình dạng, chỉ khác số kênh. Sinh bằng script để 1.000 entry là
  dữ liệu tái tạo được chứ không phải code viết tay — cùng lý do `tests/Fixtures/sparkplug/`
  có `make-fixtures.py`.

10 cycler × 100 kênh chứ không phải 1 cycler × 1.000: một cycler thật có hàng trăm kênh, không
có hàng nghìn, và số work cell cũng là một chiều cardinality mà gateway phải giải khi resolve
equipment path.

Chạy:  python deploy/seed-load/make-load-topology.py
"""

import json
import pathlib

CYCLERS = 10
CHANNELS_PER_CYCLER = 100
REVISION = 1

# Cùng site và cùng line với seed demo. Đường dẫn phải TRÙNG thì bài load mới nói về cùng một
# nhà máy: topic `NOVAVOLT/NV1/FORMATION/F1/...` là thứ gateway resolve, và nếu load topology
# đặt tên khác thì nó đang đo một plant khác.
LINE_PATH = ("NOVAVOLT", "NV1", "FORMATION", "F1")


def channels(cycler: int) -> list[dict]:
    return [
        {
            "code": f"FORM-{cycler:02d}-CH-{channel:04d}",
            "name": f"Channel {channel} of cycler {cycler}",
        }
        for channel in range(1, CHANNELS_PER_CYCLER + 1)
    ]


def cyclers() -> list[dict]:
    return [
        {
            "code": f"FORM-{cycler:02d}",
            "name": f"Formation cycler {cycler}",
            "children": channels(cycler),
        }
        for cycler in range(1, CYCLERS + 1)
    ]


def document() -> dict:
    enterprise, site, area, line = LINE_PATH

    return {
        "revision": REVISION,
        "generatedAt": "2026-08-29T00:00:00+00:00",
        "enterprise": {
            "code": enterprise,
            "name": "NovaVolt Energy",
            "children": [
                {
                    "code": site,
                    "name": "Hai Phong Gigafactory",
                    "timeZoneId": "Asia/Ho_Chi_Minh",
                    "children": [
                        {
                            "code": area,
                            "name": "Formation",
                            "children": [
                                {
                                    "code": line,
                                    "name": "Formation line 1",
                                    "children": cyclers(),
                                },
                            ],
                        },
                    ],
                },
            ],
        },
    }


def main() -> None:
    target = pathlib.Path(__file__).parent / f"factory-model.r{REVISION}.json"
    target.write_text(json.dumps(document(), ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{target}: {CYCLERS} cycler x {CHANNELS_PER_CYCLER} = {CYCLERS * CHANNELS_PER_CYCLER} channels")


if __name__ == "__main__":
    main()
