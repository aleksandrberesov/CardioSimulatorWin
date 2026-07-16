#!/usr/bin/env python3
"""Build a small, group-balanced subset of a CardioSimulator Pathologies.zip.

The full arrhythmia dataset is huge (tens of thousands of records) and wildly
skewed — e.g. ~25 000 `sinus` records but only 3 `pacemaker` ones. For quick
builds, demos, tests or teaching you want a *small* dataset that still exercises
**every** group. This tool subsamples the source zip so that:

  * every group present in the source is still present in the subset;
  * each group keeps a proportional share of its records (``--fraction``), so
    the subset roughly mirrors the original balance;
  * but **no group ever drops below a floor** (``--min-per-group``, default 4) —
    tiny groups are kept whole rather than sampled away to nothing;
  * an optional per-group cap (``--max-per-group``) keeps giant groups (sinus)
    from dominating the subset.

Instead of ``--fraction`` you can pass ``--target N`` to aim for a total record
count: every group first gets its floor, then the remaining budget is spread
proportionally by group size (largest-remainder), still capped per group.

The output is a valid Pathologies.zip: ``groups.txt`` is copied verbatim (so the
app still lists all groups), the selected ``<id>.dat`` files are copied byte-for
byte, and ``manifest.txt`` is rebuilt with only the kept entries and recomputed
aggregate counts. Selection is deterministic for a given ``--seed``.

Inputs
  --in            source Pathologies.zip (default: the bundled Assets zip)
  --out           destination zip (default: <in> with a ".subset" suffix)
  --fraction F    keep ~F of each group (0<F<=1, default 0.10)
  --target N      instead of a fraction, aim for ~N records total
  --min-per-group keep at least this many per group where available (default 4)
  --max-per-group optional per-group cap
  --select        which records to keep: random | stride | head (default random)
  --seed          RNG seed for --select random (default 0; reproducible)
  --drop-ungrouped  drop records that have no group (default: keep & sample them)
  --dry-run       print the plan, write nothing

Examples
  # 10% of every group, floor 4, from the full grouped dataset
  python subset_pathologies.py \\
      --in  E:/VLN_Project/Data/Pathologies.All.grouped.zip \\
      --out E:/VLN_Project/Data/Pathologies.subset.zip

  # aim for ~600 records total, at least 4 per group, cap any group at 150
  python subset_pathologies.py --in Pathologies.All.grouped.zip \\
      --target 600 --min-per-group 4 --max-per-group 150

  # preview only
  python subset_pathologies.py --in Pathologies.All.grouped.zip --dry-run
"""
import argparse
import os
import random
import sys
import zipfile
from collections import OrderedDict

UNGROUPED = "(ungrouped)"  # display key for records with no group field

DEFAULT_IN = os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", "..", "src", "CardioSimulator.App", "Assets", "Pathologies.zip"))


# ── manifest helpers ──────────────────────────────────────────────────────────
def parse_fields(line):
    """Parse a `key:value;key:value` manifest line into an ordered dict."""
    fields = OrderedDict()
    for part in line.split(";"):
        if ":" in part:
            k, v = part.split(":", 1)
            fields[k.strip()] = v.strip()
    return fields


def read_manifest_entries(text):
    """Return [(id, group_key, is_clinical, leads, samples, raw_line), ...] in order.

    `is_clinical` is True when the entry carries a non-empty `clinical_case`
    field (a real patient-derived case), False for the hand-authored built-in
    library records that have none.
    """
    entries = []
    for line in text.replace("\r\n", "\n").replace("\r", "\n").split("\n"):
        if not line.startswith("pathology:") or line.lstrip().startswith("#"):
            continue
        f = parse_fields(line)
        pid = f.get("pathology", "").strip()
        if not pid:
            continue
        group = f.get("group", "").strip() or UNGROUPED
        is_clinical = bool(f.get("clinical_case", "").strip())
        leads = int(f.get("leads", "0") or 0)
        samples = int(f.get("samples", "0") or 0)
        entries.append((pid, group, is_clinical, leads, samples, line))
    return entries


def rebuild_manifest(text, kept_ids):
    """Keep only the selected pathology lines; recompute aggregate header counts.

    Non-entry lines (version/format header, comments, blanks) are preserved
    verbatim and in place, so the file structure matches the source exactly.
    """
    lines = text.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    out = []
    total_paths = total_leads = total_samples = 0
    for line in lines:
        if line.startswith("pathology:") and not line.lstrip().startswith("#"):
            f = parse_fields(line)
            pid = f.get("pathology", "").strip()
            if pid not in kept_ids:
                continue
            total_paths += 1
            total_leads += int(f.get("leads", "0") or 0)
            total_samples += int(f.get("samples", "0") or 0)
        out.append(line)

    def set_header(key, value):
        for i, l in enumerate(out):
            if l.startswith(key + ":") and not l.lstrip().startswith("#"):
                out[i] = f"{key}:{value}"
                return
    set_header("pathologies", total_paths)
    set_header("total_lead_streams", total_leads)
    set_header("total_samples", total_samples)

    text_out = "\n".join(out)
    if not text_out.endswith("\n"):
        text_out += "\n"
    return text_out


# ── allocation: how many records to keep per group ────────────────────────────
def allocate_by_fraction(sizes, fraction, min_per_group, max_per_group):
    """keep = clamp(round(N*fraction), min, min(N, max)) for each group."""
    keep = {}
    for g, n in sizes.items():
        k = round(n * fraction)
        k = max(k, min_per_group)
        k = min(k, n)
        if max_per_group is not None:
            k = min(k, max_per_group)
        keep[g] = k
    return keep


def allocate_by_target(sizes, target, min_per_group, max_per_group):
    """Give every group its floor, then spread the remaining budget by group
    size using the largest-remainder method, capped by availability / max."""
    floor = {g: min(min_per_group, n) for g, n in sizes.items()}
    cap = {g: (min(n, max_per_group) if max_per_group is not None else n)
           for g, n in sizes.items()}
    keep = dict(floor)

    remaining = target - sum(keep.values())
    if remaining <= 0:
        return keep  # target already met (or below the combined floors)

    # Headroom above the floor that each group can still absorb.
    headroom = {g: cap[g] - keep[g] for g in sizes}
    pool = sum(headroom.values())
    if pool <= 0:
        return keep
    remaining = min(remaining, pool)

    # Proportional shares of the *extra* budget, weighted by group size.
    weight_total = sum(sizes[g] for g in sizes if headroom[g] > 0) or 1
    ideal = {g: remaining * sizes[g] / weight_total if headroom[g] > 0 else 0.0
             for g in sizes}
    base = {g: min(int(ideal[g]), headroom[g]) for g in sizes}
    for g in sizes:
        keep[g] += base[g]

    # Hand out the leftover one at a time, biggest fractional remainder first,
    # skipping groups that have hit their cap.
    leftover = remaining - sum(base.values())
    order = sorted(sizes, key=lambda g: (ideal[g] - int(ideal[g])), reverse=True)
    i = 0
    guard = 0
    while leftover > 0 and guard < 100000:
        guard += 1
        g = order[i % len(order)]
        i += 1
        if keep[g] < cap[g]:
            keep[g] += 1
            leftover -= 1
    return keep


# ── selection: which records within a bucket to keep ──────────────────────────
def select_ids(ids, k, mode, seed, bucket):
    """Pick k ids from `ids` (already in manifest order). Deterministic."""
    if k >= len(ids):
        return list(ids)
    if k <= 0:
        return []
    if mode == "head":
        return list(ids[:k])
    if mode == "stride":
        # Evenly spaced across the bucket so the spread of records is preserved.
        step = len(ids) / k
        return [ids[min(len(ids) - 1, int(i * step))] for i in range(k)]
    # random, seeded per-bucket so results are stable and independent of order
    rng = random.Random(f"{seed}:{bucket}")
    chosen = rng.sample(range(len(ids)), k)
    chosen.sort()  # keep manifest order in the output
    return [ids[i] for i in chosen]


# ── main ──────────────────────────────────────────────────────────────────────
def main(argv=None):
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(
        description="Build a small, group-balanced subset of a Pathologies.zip.")
    ap.add_argument("--in", dest="inp", default=None, help="source Pathologies.zip")
    ap.add_argument("--in-dir", dest="in_dir", default=None,
                    help="source is a loose dataset DIRECTORY (manifest.txt + <id>.dat). "
                         "Only manifest.txt is read — .dat bytes are never loaded — so this is "
                         "memory-safe for the full multi-GB dataset.")
    ap.add_argument("--out", dest="out", help="destination zip (default: <in>.subset.zip)")
    ap.add_argument("--out-manifest", dest="out_manifest", default=None,
                    help="write ONLY the rebuilt subset manifest.txt to this path (no zip). "
                         "Feed it to `ContentPacker pack-dir --manifest` to build the pak.")
    ap.add_argument("--fraction", type=float, default=0.10,
                    help="keep ~this share of each group (0<F<=1, default 0.10)")
    ap.add_argument("--target", type=int,
                    help="aim for ~N records total (overrides --fraction)")
    ap.add_argument("--min-per-group", type=int, default=4,
                    help="minimum records kept per group where available (default 4)")
    ap.add_argument("--max-per-group", type=int,
                    help="optional cap on records kept per group")
    ap.add_argument("--select", choices=["random", "stride", "head"], default="random",
                    help="which records to keep within a group (default random)")
    ap.add_argument("--seed", default="0", help="RNG seed for --select random (default 0)")
    ap.add_argument("--drop-ungrouped", action="store_true",
                    help="drop records that have no group (default: keep & sample them)")
    ap.add_argument("--balance-clinical", action="store_true",
                    help="also stratify by clinical-case vs non-clinical within each "
                         "group, so the built-in (non-clinical) records stay represented "
                         "at their proportion (floor still applies per group×class)")
    ap.add_argument("--dry-run", action="store_true", help="print the plan, write nothing")
    args = ap.parse_args(argv)

    if args.in_dir and args.inp:
        print("ERROR: pass either --in (zip) or --in-dir (directory), not both", file=sys.stderr)
        return 2
    if not args.in_dir and not args.inp:
        args.inp = DEFAULT_IN  # legacy default: the bundled Assets zip
    if args.in_dir and not os.path.isdir(args.in_dir):
        print(f"ERROR: source directory not found: {args.in_dir}", file=sys.stderr)
        return 2
    if args.inp and not os.path.isfile(args.inp):
        print(f"ERROR: source zip not found: {args.inp}", file=sys.stderr)
        return 2
    if not (0.0 < args.fraction <= 1.0):
        print("ERROR: --fraction must be in (0, 1]", file=sys.stderr)
        return 2
    if args.min_per_group < 0:
        print("ERROR: --min-per-group must be >= 0", file=sys.stderr)
        return 2
    if args.max_per_group is not None and args.max_per_group < args.min_per_group:
        print("ERROR: --max-per-group must be >= --min-per-group", file=sys.stderr)
        return 2

    # Resolve the output target. --out-manifest writes only a manifest.txt (memory-safe, feeds
    # `ContentPacker pack-dir`); otherwise we write a full subset zip as before.
    entries = OrderedDict()  # base name -> bytes, populated in zip mode only
    if args.out_manifest:
        out = args.out_manifest
    else:
        src_for_name = args.inp or args.in_dir
        root, ext = os.path.splitext(src_for_name)
        out = args.out or (root + ".subset" + (ext or ".zip"))

    # Read the source manifest (+ discover which .dat exist), from a dir or a zip.
    if args.in_dir:
        manifest_path = os.path.join(args.in_dir, "manifest.txt")
        if not os.path.isfile(manifest_path):
            print(f"ERROR: manifest.txt not found in {args.in_dir}", file=sys.stderr)
            return 2
        with open(manifest_path, "rb") as f:
            manifest_text = f.read().decode("utf-8", "replace")
        # Discover .dat ids by name only — never read their contents.
        dat_ids_dir = {n[:-4] for n in os.listdir(args.in_dir) if n.endswith(".dat")}
    else:
        if os.path.abspath(out) == os.path.abspath(args.inp):
            print("ERROR: --out must differ from --in", file=sys.stderr)
            return 2
        with zipfile.ZipFile(args.inp) as zin:
            for n in zin.namelist():
                base = os.path.basename(n)
                if base:
                    entries[base] = zin.read(n)
        if "manifest.txt" not in entries:
            print(f"ERROR: manifest.txt not found in {args.inp}", file=sys.stderr)
            return 2
        manifest_text = entries["manifest.txt"].decode("utf-8", "replace")
        dat_ids_dir = None
    manifest = read_manifest_entries(manifest_text)
    if not manifest:
        print("ERROR: no pathology entries found in manifest.txt", file=sys.stderr)
        return 2

    dat_ids = dat_ids_dir if dat_ids_dir is not None else {b[:-4] for b in entries if b.endswith(".dat")}

    # Bucket the entries. The sampling unit is the group, or — with
    # --balance-clinical — the (group, clinical?) pair, so the built-in
    # non-clinical records keep their share instead of being sampled away.
    # Manifest order is preserved within each bucket.
    buckets = OrderedDict()       # bucket key -> [pid, ...]
    bucket_group = {}             # bucket key -> group
    bucket_clinical = {}          # bucket key -> True/False/None
    missing_dat = []
    for pid, group, is_clinical, _leads, _samples, _line in manifest:
        if pid not in dat_ids:
            missing_dat.append(pid)
            continue
        if group == UNGROUPED and args.drop_ungrouped:
            continue
        if args.balance_clinical:
            key = f"{group}\x01{int(is_clinical)}"
            clin = is_clinical
        else:
            key = group
            clin = None
        if key not in buckets:
            buckets[key] = []
            bucket_group[key] = group
            bucket_clinical[key] = clin
        buckets[key].append(pid)

    if not buckets:
        print("ERROR: nothing to sample (all entries dropped/missing)", file=sys.stderr)
        return 2

    sizes = {k: len(ids) for k, ids in buckets.items()}

    # Decide how many to keep per bucket.
    if args.target is not None:
        keep_counts = allocate_by_target(
            sizes, args.target, args.min_per_group, args.max_per_group)
        mode_desc = f"target≈{args.target}"
    else:
        keep_counts = allocate_by_fraction(
            sizes, args.fraction, args.min_per_group, args.max_per_group)
        mode_desc = f"fraction={args.fraction:g}"

    # Select the actual ids per bucket.
    kept_ids = set()
    kept_per_bucket = {}
    for key, ids in buckets.items():
        chosen = select_ids(ids, keep_counts[key], args.select, args.seed, key)
        kept_per_bucket[key] = chosen
        kept_ids.update(chosen)

    def pct(k, n):
        return (100.0 * k / n) if n else 0.0

    # ── report ────────────────────────────────────────────────────────────────
    print(f"Source : {args.inp}")
    print(f"Output : {out}")
    print(f"Mode   : {mode_desc}, min/group={args.min_per_group}"
          + (f", max/group={args.max_per_group}" if args.max_per_group else "")
          + f", select={args.select}"
          + (f", seed={args.seed}" if args.select == "random" else "")
          + (", balance-clinical" if args.balance_clinical else ""))
    print()

    # group -> its bucket keys (clinical bucket first), plus per-group totals
    groups = OrderedDict()
    for key in buckets:
        groups.setdefault(bucket_group[key], []).append(key)
    group_src = {g: sum(sizes[k] for k in ks) for g, ks in groups.items()}
    group_order = sorted(groups, key=lambda g: group_src[g], reverse=True)

    total_src = sum(sizes.values())
    total_kept = len(kept_ids)

    if args.balance_clinical:
        print(f"  {'group':<14}{'class':<13}{'source':>8}{'kept':>8}{'%':>7}")
        print(f"  {'-'*14}{'-'*13}{'-'*8}{'-'*8}{'-'*7}")
        for g in group_order:
            for key in sorted(groups[g], key=lambda k: not bucket_clinical[k]):
                n, k = sizes[key], len(kept_per_bucket[key])
                cls = "clinical" if bucket_clinical[key] else "non-clinical"
                print(f"  {g:<14}{cls:<13}{n:>8}{k:>8}{pct(k, n):>6.1f}%")
        print(f"  {'-'*14}{'-'*13}{'-'*8}{'-'*8}{'-'*7}")
        # clinical vs non-clinical rollup
        for flag, label in ((True, "clinical"), (False, "non-clinical")):
            keys = [k for k in buckets if bucket_clinical[k] is flag]
            n = sum(sizes[k] for k in keys)
            k = sum(len(kept_per_bucket[key]) for key in keys)
            print(f"  {'(all)':<14}{label:<13}{n:>8}{k:>8}{pct(k, n):>6.1f}%")
        print(f"  {'-'*14}{'-'*13}{'-'*8}{'-'*8}{'-'*7}")
        print(f"  {'TOTAL':<14}{'':<13}{total_src:>8}{total_kept:>8}{pct(total_kept, total_src):>6.1f}%")
    else:
        print(f"  {'group':<14}{'source':>8}{'kept':>8}{'%':>7}")
        print(f"  {'-'*14}{'-'*8}{'-'*8}{'-'*7}")
        for g in group_order:
            key = groups[g][0]
            n, k = sizes[key], len(kept_per_bucket[key])
            print(f"  {g:<14}{n:>8}{k:>8}{pct(k, n):>6.1f}%")
        print(f"  {'-'*14}{'-'*8}{'-'*8}{'-'*7}")
        print(f"  {'TOTAL':<14}{total_src:>8}{total_kept:>8}{pct(total_kept, total_src):>6.1f}%")
    print()

    if missing_dat:
        print(f"NOTE: {len(missing_dat)} manifest entr(y/ies) had no matching .dat "
              f"and were ignored (e.g. {missing_dat[0]}).", file=sys.stderr)

    if args.dry_run:
        print(f"Dry run: would write {total_kept} pathologies to {out}")
        return 0

    new_manifest = rebuild_manifest(manifest_text, kept_ids).encode("utf-8")

    # ── manifest-only output (feeds `ContentPacker pack-dir --manifest`) ─────────
    if args.out_manifest:
        os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
        with open(out, "wb") as f:
            f.write(new_manifest)
        print(f"Wrote manifest {out} ({os.path.getsize(out):,} bytes): "
              f"{total_kept} pathologies across {len(groups)} group(s).")
        return 0

    # ── write the subset zip (legacy zip-in/zip-out path) ───────────────────────
    written = 0
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zout:
        for base, data in entries.items():
            if base == "manifest.txt":
                zout.writestr("manifest.txt", new_manifest)
            elif base.endswith(".dat"):
                if base[:-4] in kept_ids:
                    zout.writestr(base, data)
                    written += 1
            else:
                zout.writestr(base, data)  # groups.txt and any other aux files

    print(f"Wrote {out} ({os.path.getsize(out):,} bytes): "
          f"{written} pathologies across {len(groups)} group(s).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
