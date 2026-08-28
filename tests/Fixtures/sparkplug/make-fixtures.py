#!/usr/bin/env python3
"""Regenerate the Sparkplug B fixtures next to this script.

Read tests/Fixtures/sparkplug/README.md first. The short version: these bytes must NOT come from
our own encoder, so they are produced by pysparkplug, which carries its own copy of the Sparkplug
schema. If src/Platform/Nvm.Sparkplug/proto/sparkplug_b.proto ever drifts from the specification,
these fixtures stop decoding and the C# test goes red.

    python -m venv .venv && .venv/Scripts/pip install pysparkplug==0.6.1
    .venv/Scripts/python tests/Fixtures/sparkplug/make-fixtures.py

Not part of `make build`, `make test` or CI. It runs when a fixture has to change, and never
otherwise -- a fixture that is regenerated on every build is a fixture that proves nothing.
"""

import pathlib

from pysparkplug import DataType, DBirth, DData, Metric

# 2026-08-28T07:15:30.500Z and +2s, as Sparkplug timestamps: milliseconds since the Unix epoch, UTC.
# Written as literals rather than computed from a date string so the fixture bytes never depend on
# the machine's clock or timezone.
BIRTH_MS = 1787901330500
DATA_MS = 1787901332500

# Values that binary32 represents exactly, so the C# assertions can use exact equality instead of a
# tolerance. A tolerance would also pass if the decoder read the wrong four bytes and landed nearby.
BIRTH_METRICS = (
    Metric(timestamp=BIRTH_MS, name="Formation/Voltage", alias=1, datatype=DataType.FLOAT, value=3.6875),
    Metric(timestamp=BIRTH_MS, name="Formation/Current", alias=2, datatype=DataType.FLOAT, value=1.25),
    Metric(timestamp=BIRTH_MS, name="Formation/Temperature", alias=3, datatype=DataType.FLOAT, value=31.5),
    Metric(timestamp=BIRTH_MS, name="Formation/StepIndex", alias=4, datatype=DataType.INT32, value=2),
    Metric(
        timestamp=BIRTH_MS,
        name="Formation/CellSerial",
        alias=5,
        datatype=DataType.STRING,
        value="NV1CL16238A00123",
    ),
)

# Report-by-exception: voltage and temperature moved, current and step index did not, so they are
# not on the wire at all. No names, no datatypes -- only the aliases the DBIRTH established.
DATA_METRICS = (
    Metric(timestamp=DATA_MS, name=None, alias=1, datatype=DataType.FLOAT, value=3.71875),
    Metric(timestamp=DATA_MS, name=None, alias=3, datatype=DataType.FLOAT, value=31.75),
)


def main() -> None:
    here = pathlib.Path(__file__).parent

    # include_dtypes: a DBIRTH declares what each metric is; a DDATA relies on that declaration.
    files = {
        "dbirth-form-01-ch-0142.bin": DBirth(timestamp=BIRTH_MS, seq=1, metrics=BIRTH_METRICS).encode(
            include_dtypes=True
        ),
        "ddata-form-01-ch-0142.bin": DData(timestamp=DATA_MS, seq=2, metrics=DATA_METRICS).encode(
            include_dtypes=False
        ),
    }

    for name, payload in files.items():
        (here / name).write_bytes(payload)
        print(f"{name}: {len(payload)} bytes")


if __name__ == "__main__":
    main()
