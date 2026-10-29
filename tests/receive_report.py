"""Summarize receptions from Xfer diagnostic traces (trace-*.jsonl).

python tests/receive_report.py TRACE_OR_FOLDER [...] [--table]

For each camera start in a trace: seconds from camera start to receive.complete (or camera stop), seconds from the
first frame of the session, the transmitted payload (from the transfer's correlation key) per second, pictures
captured, pictures read (QR codes or FX2 pictures), new and duplicate fountain frames, and stalls. The trace keeps
5-second aggregates, so stalls are counted on them: a 5-second bucket with at most 5 new frames is low, and low
buckets in a row lasting 10 s or more count as one stall.
--table prints the 5-second buckets.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path


def rows_of(path: Path) -> list[dict]:
    rows = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.strip():
            try:
                rows.append(json.loads(line))
            except ValueError:
                pass
    return rows


def traces(arguments: list[str]) -> list[Path]:
    found = []
    for argument in arguments:
        path = Path(argument)
        found += sorted(path.rglob("trace-*.jsonl")) if path.is_dir() else [path]
    return found


def report(path: Path, table: bool) -> None:
    rows = rows_of(path)
    starts = [r for r in rows if r["event"] == "camera.start"]
    for index, start in enumerate(starts):
        begin = start["monoMs"]
        end_candidates = [r for r in rows if r["monoMs"] > begin and r["event"] in ("camera.stop", "receive.complete")]
        later = [r["monoMs"] for r in starts[index + 1:]]
        limit = later[0] if later else float("inf")
        end_candidates = [r for r in end_candidates if r["monoMs"] < limit]
        complete = next((r for r in end_candidates if r["event"] == "receive.complete"), None)
        stop = next((r for r in end_candidates if r["event"] == "camera.stop"), None)
        end = complete or stop
        end_ms = end["monoMs"] if end else max(r["monoMs"] for r in rows)
        session = next((r for r in rows if begin <= r["monoMs"] <= end_ms and r["event"] == "receive.session"), None)
        transfer = (complete or session or {}).get("correlation", "")
        parts = transfer.split(":")
        payload = int(parts[3]) if len(parts) == 4 and parts[3].isdigit() else 0
        blocks = int(parts[1]) if len(parts) == 4 and parts[1].isdigit() else 0
        buckets: dict[int, dict] = {}
        for r in rows:
            data = r.get("data")
            if not (begin <= r["monoMs"] <= end_ms + 5000 and isinstance(data, dict) and "count" in data and "totalMs" in data):
                continue
            bucket = buckets.setdefault(r["monoMs"] // 1000, {"utc": r["utc"][11:19]})
            bucket[r["event"]] = data
        captured = read_qr = read_fx2 = new = duplicate = 0
        stalls = run = longest = low = counted = 0
        lines = []
        for second in sorted(buckets):
            bucket = buckets[second]
            c = bucket.get("camera.capture", {}).get("count", 0)
            if c == 0:
                continue
            counted += 1
            n = bucket.get("receive.accepted", {}).get("count", 0)
            q = bucket.get("camera.qr_read", {}).get("count", 0)
            f = bucket.get("camera.fx2_read", {}).get("count", 0)
            solved = bucket.get("receive.accepted", {}).get("last", {}).get("solved", "")
            captured += c; read_qr += q; read_fx2 += f; new += n; duplicate += bucket.get("receive.duplicate", {}).get("count", 0)
            if n <= 5:
                run += 5; low += 1
            else:
                if run >= 10:
                    stalls += 1
                longest = max(longest, run); run = 0
            lines.append(f"  {bucket['utc']}  captured {c:4d}  qr {q:4d}  fx2 {f:4d}  new {n:5d}  solved {solved}")
        if run >= 10:
            stalls += 1
        longest = max(longest, run)
        seconds = (end_ms - begin) / 1000
        from_session = (end_ms - session["monoMs"]) / 1000 if session else 0
        outcome = "complete" if complete else "stopped" if stop else "running"
        print(f"{path.name} reception {index + 1}: {outcome}, {seconds:.1f} s from camera start ({from_session:.1f} s from first frame), "
              f"{payload:,} B in {blocks:,} blocks, {payload / max(seconds, 0.001) / 1024:.1f} KiB/s; captured {captured}, "
              f"QR read {read_qr}, FX2 pictures {read_fx2}, new {new} ({new / max(seconds, 0.001):.1f}/s), duplicate {duplicate}; "
              f"stalls >= 10 s: {stalls}, longest low run {longest} s, low buckets {low}/{counted}")
        if table:
            print("\n".join(lines))


def main() -> int:
    arguments = [a for a in sys.argv[1:] if a != "--table"]
    if not arguments:
        print(__doc__)
        return 2
    for path in traces(arguments):
        report(path, "--table" in sys.argv)
    return 0


if __name__ == "__main__":
    sys.exit(main())
