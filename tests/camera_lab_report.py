"""Offline CameraLab consistency checks, per-second summaries and PNG plots. Never opens a camera."""
import argparse
import collections
import csv
import json
from pathlib import Path
import statistics


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def analyze(directory, plots):
    summary = read_json(directory / "summary.json")
    frames = list(csv.DictReader((directory / "frames.csv").open(encoding="utf-8", newline="")))
    captures = list(csv.DictReader((directory / "captures.csv").open(encoding="utf-8", newline="")))
    controls = [json.loads(line) for line in (directory / "controls.jsonl").read_text(encoding="utf-8").splitlines()]
    assert len(captures) == summary["Captured"], "Captured count differs from CSV"
    assert sum(r["queued"] == "0" for r in captures) == summary["DroppedBeforeDecode"]
    assert len(frames) == len(captures) - summary["DroppedBeforeDecode"]
    selections = [
        (summary["All"], frames),
        (summary["Measurement"], [r for r in frames if r["phase"] == "measurement"]),
    ]
    for phase, key in [("auto", "AutoMeasurement"), ("locked", "LockedMeasurement")]:
        selections.append((summary["AfTrial"][key], [
            r for r in frames if r["phase"] == "measurement" and r["focus_phase"] == phase
        ]))
    for stats, selected in selections:
        ok = [r for r in selected if r["xfer_ok"] == "1"]
        assert len(selected) == stats["Analyzed"]
        assert len(ok) == stats["XferDecoded"]
        assert len({r["payload_sha256"] for r in ok}) == stats["UniqueQr"]
        assert len(ok) - stats["UniqueQr"] == stats["DuplicateQr"]
        if selected:
            assert abs(len(ok) / len(selected) - stats["DecodeRate"]) < 1e-12

    bins = collections.defaultdict(list)
    for row in frames:
        bins[int(float(row["since_first_ms"]) // 1000)].append(row)
    seconds = []
    for second, group in sorted(bins.items()):
        ok = [r for r in group if r["xfer_ok"] == "1"]
        boundaries = [int(r["boundary_row"]) for r in group if r["boundary_row"]]
        seconds.append(dict(
            second=second, analyzed=len(group), read=len(ok), rate=len(ok) / len(group),
            unique_in_second=len({r["payload_sha256"] for r in ok}),
            boundary_candidates=len(boundaries),
            boundary_min=min(boundaries) if boundaries else None,
            boundary_median=statistics.median(boundaries) if boundaries else None,
            boundary_max=max(boundaries) if boundaries else None,
        ))
    with (directory / "seconds.csv").open("w", encoding="utf-8", newline="") as out:
        writer = csv.DictWriter(out, fieldnames=list(seconds[0]))
        writer.writeheader()
        writer.writerows(seconds)

    boundaries = [r for r in frames if r["boundary_row"]]
    report = {
        "ArtifactConsistency": "PASS",
        "BoundaryReasons": dict(collections.Counter(r["boundary_reason"] for r in frames)),
        "AcceptedBoundaryCandidates": len(boundaries),
        "CandidateRowMinimum": min(int(r["boundary_row"]) for r in boundaries) if boundaries else None,
        "CandidateRowMedian": statistics.median(int(r["boundary_row"]) for r in boundaries) if boundaries else None,
        "CandidateRowMaximum": max(int(r["boundary_row"]) for r in boundaries) if boundaries else None,
        "ConfidenceMedian": statistics.median(float(r["boundary_confidence"]) for r in boundaries) if boundaries else None,
        "ControlSampleRanges": {},
        "AfLock": next((r for r in controls if r["Stage"] == "af_lock"), None),
        "Caveat": "Boundary candidates are image-difference change points, not verified mixed-QR seams. "
                  "Repeated candidates near the ROI margin can reflect QR row structure. "
                  "AF before/after with automatic exposure does not isolate a causal focus effect.",
    }
    for name in ("exposure", "focus", "gain"):
        samples = [r["State"]["Value"] for r in controls if r["Name"] == name and r["Stage"] == "sample"]
        report["ControlSampleRanges"][name] = {"min": min(samples), "max": max(samples)} if samples else None
    (directory / "analysis.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    if plots:
        plot(directory, frames, controls, summary, seconds)
    print(f"{directory}: PASS; {len(frames)} analyzed, {len(boundaries)} boundary candidates")


def plot(directory, frames, controls, summary, seconds):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    import numpy as np

    first = summary["FirstFrameAtMs"]
    settle = read_json(directory / "run.json")["Options"]["AfLockSettle"]
    lock = summary["AfTrial"]["AfLockAtMs"]
    lock_s = (lock - first) / 1000 if lock is not None else None
    fig, axes = plt.subplots(3, 1, figsize=(12, 8), sharex=True, constrained_layout=True)
    axes[0].plot([s["second"] + .5 for s in seconds], [s["rate"] * 100 for s in seconds], "o-")
    axes[0].set(ylabel="Decoded / analyzed (%)", ylim=(0, 100))
    for name in ("focus", "gain"):
        samples = [r for r in controls if r["Name"] == name and r["Stage"] == "sample"]
        axes[1].plot([(r["AtMs"] - first) / 1000 for r in samples], [r["State"]["Value"] for r in samples],
                     ".-", label=name)
    axes[1].set_ylabel("Driver readback")
    axes[1].legend()
    samples = [r for r in controls if r["Name"] == "exposure" and r["Stage"] == "sample"]
    axes[2].plot([(r["AtMs"] - first) / 1000 for r in samples], [r["State"]["Value"] for r in samples], ".-")
    axes[2].set(ylabel="Exposure (log2 seconds)", xlabel="Seconds since first delivered frame")
    for ax in axes:
        ax.grid(alpha=.2)
        if lock_s is not None:
            ax.axvline(lock_s, color="red", linestyle="--", label="AF lock")
            ax.axvspan(lock_s, lock_s + settle, color="red", alpha=.08)
    fig.suptitle(f"{directory.name}: read rate and controls; dashed red = AF lock")
    fig.savefig(directory / "af-overview.png", dpi=150)
    plt.close(fig)

    profiles_path = directory / "row-differences.jsonl"
    profiles = [json.loads(line) for line in profiles_path.read_text().splitlines()] if profiles_path.exists() else []
    if not profiles:
        return
    fig, axes = plt.subplots(2, 1, figsize=(12, 8), sharex=True, constrained_layout=True)
    times = np.array([(r["At"] - first) / 1000 for r in profiles])
    roi = profiles[0]["Region"]
    assert all(r["Region"] == roi for r in profiles), "Plot expects a fixed ROI"
    data = np.array([r["RowMeanAbsoluteDifference"] for r in profiles]).T
    # Explicit time coordinates preserve gaps; no artificial uniform spacing.
    mesh = axes[0].pcolormesh(times, np.arange(roi["Y"], roi["Y"] + roi["Height"]),
                             data, shading="nearest", cmap="magma", vmin=0, vmax=np.percentile(data, 98))
    fig.colorbar(mesh, ax=axes[0], label="Mean absolute gray difference")
    candidates = [r for r in frames if r["boundary_row"]]
    axes[1].scatter([float(r["since_first_ms"]) / 1000 for r in candidates],
                    [int(r["boundary_row"]) for r in candidates],
                    c=[float(r["boundary_confidence"]) for r in candidates], s=8, vmin=0, vmax=1, cmap="viridis")
    for ax in axes:
        ax.set_ylim(roi["Y"] + roi["Height"], roi["Y"])
        ax.set_ylabel("Image row (0-based)")
        if lock_s is not None:
            ax.axvline(lock_s, color="cyan", linestyle="--")
    axes[1].set_xlabel("Seconds since first delivered frame")
    axes[1].set_title("Accepted candidates; color = explained variance (not probability)")
    fig.suptitle(f"{directory.name}: adjacent-frame differences; candidates are not verified QR seams")
    fig.savefig(directory / "boundary-overview.png", dpi=150)
    plt.close(fig)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directories", type=Path, nargs="+")
    parser.add_argument("--plots", action="store_true", help="requires matplotlib and numpy")
    args = parser.parse_args()
    for directory in args.directories:
        analyze(directory, args.plots)
