#!/usr/bin/env python3
"""Correct ECG signal boundaries (beginnings and endings) in a CardioSimulator Pathologies zip.

This tool checks the first and last N points (default: 10) of ECG signals in pathology .dat files.
If they deviate from the baseline (default: 1024 ADC), it smoothly interpolates them to the baseline
to prevent graphical breaks or jumps when drawn on the simulator monitor.

Interpolation formula:
    For start (i in [0, M-1]):
        w = i / M
        new_val = round(w * old_val + (1 - w) * baseline)
    For end (i in [N-M, N-1]):
        w = (N - 1 - i) / M
        new_val = round(w * old_val + (1 - w) * baseline)
    where M = min(window, N // 2).

Inputs:
  --in           source Pathologies.zip          (default: the repo asset)
  --out          destination zip                 (default: <in stem>.corrected.zip)
  --window       number of points to interpolate (default: 10)
  --baseline     ADC isoline to align to         (default: 1024)
  --threshold    only correct if deviation is > threshold (default: 0)
  --ids          comma-separated pathology ids to process (default: all)
  --dry-run      report what would change, but do not write the zip
"""
import argparse
import os
import sys
import zipfile

DEFAULT_BASELINE = 1024
DEFAULT_WINDOW = 10
DEFAULT_THRESHOLD = 0

DEFAULT_IN = os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", "..", "src", "CardioSimulator.App", "Assets", "Pathologies.zip"))


def correct_points_array(pts, baseline, window, threshold):
    """Correct the start and end of a points array to the baseline.

    Returns (new_pts, corrected_start, corrected_end).
    """
    N = len(pts)
    if N == 0:
        return pts, False, False

    corrected_start = False
    corrected_end = False

    # Copy list to avoid mutating the original
    pts = list(pts)

    # Check start boundary
    if abs(pts[0] - baseline) > threshold:
        corrected_start = True
        M = min(window, N // 2)
        if M > 0:
            for i in range(M):
                w = i / M
                pts[i] = int(round(w * pts[i] + (1.0 - w) * baseline))
        else:
            # Single-point signal fallback
            pts[0] = baseline

    # Check end boundary
    if N > 1 and abs(pts[-1] - baseline) > threshold:
        corrected_end = True
        M = min(window, N // 2)
        if M > 0:
            for i in range(N - M, N):
                w = (N - 1 - i) / M
                pts[i] = int(round(w * pts[i] + (1.0 - w) * baseline))
        else:
            pts[-1] = baseline

    return pts, corrected_start, corrected_end


def correct_dat_text(text, baseline, window, threshold):
    """Scan and correct every `points:` line in a .dat text.

    Returns (new_text, list_of_changes).
    """
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    new_lines = []
    changes = []

    current_lead = "unknown"
    for line in text.split("\n"):
        if line.startswith("lead:"):
            current_lead = line.split(":", 1)[1].strip()
            new_lines.append(line)
        elif line.startswith("points:"):
            payload = line[len("points:"):]
            original_pts = []
            for tok in payload.split(","):
                tok = tok.strip()
                if tok:
                    original_pts.append(int(tok))

            if not original_pts:
                new_lines.append(line)
                continue

            orig_start = original_pts[0]
            orig_end = original_pts[-1]

            new_pts, corr_start, corr_end = correct_points_array(
                original_pts, baseline, window, threshold)

            if corr_start or corr_end:
                changes.append({
                    "lead": current_lead,
                    "corr_start": corr_start,
                    "corr_end": corr_end,
                    "orig_start": orig_start,
                    "orig_end": orig_end,
                    "new_start": new_pts[0],
                    "new_end": new_pts[-1]
                })
                new_payload = ",".join(str(x) for x in new_pts)
                new_lines.append("points:" + new_payload)
            else:
                new_lines.append(line)
        else:
            new_lines.append(line)

    return "\n".join(new_lines), changes


def main(argv=None):
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(
        description="Correct ECG signal boundaries in a CardioSimulator Pathologies zip.")
    ap.add_argument("--in", dest="inp", default=DEFAULT_IN, help="source Pathologies.zip")
    ap.add_argument("--out", dest="out", help="destination zip (default: <in stem>.corrected.zip)")
    ap.add_argument("--window", type=int, default=DEFAULT_WINDOW,
                    help=f"number of boundary points to interpolate (default: {DEFAULT_WINDOW})")
    ap.add_argument("--baseline", type=int, default=DEFAULT_BASELINE,
                    help=f"ADC isoline to align to (default: {DEFAULT_BASELINE})")
    ap.add_argument("--threshold", type=int, default=DEFAULT_THRESHOLD,
                    help=f"only correct if deviation is > threshold (default: {DEFAULT_THRESHOLD})")
    ap.add_argument("--ids", help="comma-separated pathology ids to limit correction to")
    ap.add_argument("--dry-run", action="store_true",
                    help="report changes but do not write the zip")
    args = ap.parse_args(argv)

    if not os.path.isfile(args.inp):
        print(f"ERROR: source zip not found: {args.inp}", file=sys.stderr)
        return 2
    if args.window <= 0:
        print("ERROR: window must be > 0", file=sys.stderr)
        return 2

    out = args.out or (os.path.splitext(args.inp)[0] + ".corrected.zip")
    if not args.dry_run and os.path.abspath(out) == os.path.abspath(args.inp):
        print("ERROR: --out must differ from --in", file=sys.stderr)
        return 2

    only = None
    if args.ids:
        only = {s.strip().lower() for s in args.ids.split(",") if s.strip()}

    # Read every entry from the source zip
    with zipfile.ZipFile(args.inp) as zin:
        infos = zin.infolist()
        entries = [(info.filename, zin.read(info.filename)) for info in infos]

    dat_total = 0
    dat_modified = 0
    leads_modified_count = 0
    new_entries = []
    matched_ids = set()

    print(f"Scanning archive: {args.inp}")
    print(f"Parameters: baseline={args.baseline}, window={args.window}, threshold={args.threshold}\n")

    for name, data in entries:
        base = os.path.basename(name)
        if base.endswith(".dat"):
            dat_total += 1
            pid = base[:-4]
            pid_lower = pid.lower()
            if only is not None and pid_lower not in only:
                new_entries.append((name, data))
                continue
            matched_ids.add(pid_lower)

            text = data.decode("utf-8")
            new_text, changes = correct_dat_text(text, args.baseline, args.window, args.threshold)

            if changes:
                dat_modified += 1
                leads_modified_count += len(changes)
                print(f"[{base}] modified:")
                for c in changes:
                    change_desc = []
                    if c["corr_start"]:
                        change_desc.append(f"start ({c['orig_start']} -> {c['new_start']})")
                    if c["corr_end"]:
                        change_desc.append(f"end ({c['orig_end']} -> {c['new_end']})")
                    print(f"  - Lead {c['lead']}: " + ", ".join(change_desc))
                new_entries.append((name, new_text.encode("utf-8")))
            else:
                new_entries.append((name, data))
        else:
            new_entries.append((name, data))

    if only is not None:
        missing = sorted(only - matched_ids)
        if missing:
            print(f"\nWARNING: --ids not found as .dat in zip: {', '.join(missing)}",
                  file=sys.stderr)

    print("\nSummary:")
    print(f"  Total .dat files scanned: {dat_total}")
    print(f"  Modified .dat files:     {dat_modified}")
    print(f"  Total leads corrected:    {leads_modified_count}")

    if dat_modified == 0:
        print("\nNo signal boundary corrections needed.")
        return 0

    if args.dry_run:
        print(f"\nDry run: would write corrected archive to: {out}")
        return 0

    # Write the new zip
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zout:
        for name, data in new_entries:
            zout.writestr(name, data)

    print(f"\nSuccessfully wrote corrected zip to: {out} ({os.path.getsize(out):,} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
