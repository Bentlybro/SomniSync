#!/usr/bin/env python3
"""
Diff two NetworkDebug CSV dumps to find inter-client NPC position delta
and clock-offset skew.

Each client's NetworkDebug writes one CSV per session to
Application.persistentDataPath (printed to the Unity console on first
write). After an overlapping test run, drop both files here:

    python tools/netdbg_diff.py netdbg_A_*.csv netdbg_B_*.csv [--out diff.csv]

The script nearest-neighbor-joins rows on serverT per npc_id, computes
Euclidean position delta, and reports summary stats. Each client's
serverT is its own local estimate of server time — if the estimates
diverge, that shows up in the per-row "offset_delta" column.
"""
from __future__ import annotations

import argparse
import bisect
import csv
import math
import statistics
import sys
from pathlib import Path


def load(path: Path) -> tuple[list[dict], str]:
    rows: list[dict] = []
    tag = ""
    with path.open(newline="", encoding="utf-8") as fh:
        for r in csv.DictReader(fh):
            # Skip clock-only rows (no NPC) — we handle clock skew separately.
            npc_id = r.get("npc_id") or ""
            if npc_id == "":
                rows.append({"_clock_only": True, **r})
                continue
            try:
                rows.append({
                    "_clock_only": False,
                    "npc_id": npc_id,
                    "serverT": float(r["serverT"]),
                    "offset": float(r["offset"]),
                    "pos": (float(r["pos_x"]), float(r["pos_y"]), float(r["pos_z"])),
                    "name": r.get("npc_name", ""),
                })
            except (ValueError, KeyError):
                continue
            if not tag:
                tag = r.get("tag", "")
    return rows, tag or path.stem


def index_by_npc(rows: list[dict]) -> dict[str, list[dict]]:
    idx: dict[str, list[dict]] = {}
    for r in rows:
        if r["_clock_only"]:
            continue
        idx.setdefault(r["npc_id"], []).append(r)
    for v in idx.values():
        v.sort(key=lambda r: r["serverT"])
    return idx


def nearest(sorted_rows: list[dict], serverT: float) -> dict | None:
    if not sorted_rows:
        return None
    keys = [r["serverT"] for r in sorted_rows]
    i = bisect.bisect_left(keys, serverT)
    candidates = []
    if i < len(keys):
        candidates.append(sorted_rows[i])
    if i > 0:
        candidates.append(sorted_rows[i - 1])
    return min(candidates, key=lambda r: abs(r["serverT"] - serverT))


def dist3(a: tuple[float, float, float], b: tuple[float, float, float]) -> float:
    return math.sqrt(sum((x - y) * (x - y) for x, y in zip(a, b)))


def percentile(values: list[float], p: float) -> float:
    if not values:
        return float("nan")
    s = sorted(values)
    k = (len(s) - 1) * (p / 100.0)
    lo = int(math.floor(k))
    hi = int(math.ceil(k))
    if lo == hi:
        return s[lo]
    return s[lo] + (s[hi] - s[lo]) * (k - lo)


def fmt(v: float) -> str:
    return "nan" if math.isnan(v) else f"{v:.4f}"


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("csv_a", type=Path, help="Client A's netdbg_*.csv")
    p.add_argument("csv_b", type=Path, help="Client B's netdbg_*.csv")
    p.add_argument("--max-skew", type=float, default=2.0,
                   help="Drop pairs whose serverT differ by more than this (seconds). Default 2.0.")
    p.add_argument("--out", type=Path,
                   help="Optional output CSV of every matched pair with position delta.")
    args = p.parse_args()

    a_rows, a_tag = load(args.csv_a)
    b_rows, b_tag = load(args.csv_b)
    a_idx = index_by_npc(a_rows)
    b_idx = index_by_npc(b_rows)
    common = sorted(set(a_idx) & set(b_idx))
    a_only = sorted(set(a_idx) - set(b_idx))
    b_only = sorted(set(b_idx) - set(a_idx))

    print(f"A={args.csv_a.name} (tag={a_tag}, {sum(len(v) for v in a_idx.values())} npc rows)")
    print(f"B={args.csv_b.name} (tag={b_tag}, {sum(len(v) for v in b_idx.values())} npc rows)")
    if a_only:
        print(f"  only-in-A npc_ids: {a_only}")
    if b_only:
        print(f"  only-in-B npc_ids: {b_only}")
    if not common:
        print("No overlapping npc_ids. Nothing to diff.")
        return 1

    out_writer = None
    out_fh = None
    if args.out:
        out_fh = args.out.open("w", newline="", encoding="utf-8")
        out_writer = csv.writer(out_fh)
        out_writer.writerow([
            "npc_id", "npc_name", "serverT_A", "serverT_B", "serverT_skew",
            "offset_A", "offset_B", "offset_delta",
            "pos_dist", "dx", "dy", "dz",
        ])

    print()
    print(f"{'npc_id':>8}  {'name':<20}  {'n':>5}  {'mean':>8}  {'median':>8}  {'p95':>8}  {'max':>8}  {'meanSkew':>10}")
    print("-" * 92)

    all_offset_deltas: list[float] = []
    for npc_id in common:
        a_list = a_idx[npc_id]
        b_list = b_idx[npc_id]
        dists: list[float] = []
        skews: list[float] = []
        offset_deltas: list[float] = []
        name = a_list[0]["name"]
        for ra in a_list:
            rb = nearest(b_list, ra["serverT"])
            if rb is None:
                continue
            skew = ra["serverT"] - rb["serverT"]
            if abs(skew) > args.max_skew:
                continue
            d = dist3(ra["pos"], rb["pos"])
            offset_delta = ra["offset"] - rb["offset"]
            dists.append(d)
            skews.append(skew)
            offset_deltas.append(offset_delta)
            all_offset_deltas.append(offset_delta)
            if out_writer is not None:
                out_writer.writerow([
                    npc_id, name, f"{ra['serverT']:.6f}", f"{rb['serverT']:.6f}", f"{skew:.6f}",
                    f"{ra['offset']:.6f}", f"{rb['offset']:.6f}", f"{offset_delta:.6f}",
                    f"{d:.6f}",
                    f"{ra['pos'][0] - rb['pos'][0]:.4f}",
                    f"{ra['pos'][1] - rb['pos'][1]:.4f}",
                    f"{ra['pos'][2] - rb['pos'][2]:.4f}",
                ])
        if not dists:
            continue
        print(
            f"{npc_id:>8}  {name[:20]:<20}  {len(dists):>5}  "
            f"{fmt(statistics.mean(dists)):>8}  {fmt(statistics.median(dists)):>8}  "
            f"{fmt(percentile(dists, 95)):>8}  {fmt(max(dists)):>8}  "
            f"{fmt(statistics.mean(skews)):>10}"
        )

    if all_offset_deltas:
        print()
        print("Clock skew between clients (A.offset - B.offset):")
        print(f"  mean   = {statistics.mean(all_offset_deltas):+.6f} s")
        if len(all_offset_deltas) > 1:
            print(f"  stdev  = {statistics.stdev(all_offset_deltas):.6f} s")
        print(f"  min    = {min(all_offset_deltas):+.6f} s")
        print(f"  max    = {max(all_offset_deltas):+.6f} s")
        print(f"  range  = {max(all_offset_deltas) - min(all_offset_deltas):.6f} s")
        print()
        print("Interpretation:")
        print("  * stable mean with tiny stdev -> per-client bias (asymmetric network path).")
        print("    Position delta ~ wanderSpeed * mean_offset_delta.")
        print("  * large stdev or widening range -> EMA is still tracking (noise or real drift).")
        print("    Consider lower alpha or a median-of-N filter.")

    if out_fh is not None:
        out_fh.close()
        print(f"\nPer-pair CSV: {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
