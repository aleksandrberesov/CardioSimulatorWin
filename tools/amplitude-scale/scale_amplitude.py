#!/usr/bin/env python3
"""Rescale ECG signal amplitude in a CardioSimulator Pathologies.zip.

Every pathology `.dat` stores its waveform as raw ADC samples on a baseline of
1024 counts (1024 counts/mV; see EcgCalibration / manifest `baseline:`). This
tool multiplies each sample's *deviation from the baseline* by a scale factor,
so the trace grows taller above the isoline and deeper below it while the
isoline itself stays put:

    new = int(baseline + (value - baseline) * scale)

That single expression covers both halves the request describes — above the
isoline it is (value - 1024) * scale, below it (1024 - value) * scale — because
the deviation simply changes sign. A sample sitting exactly on the baseline is
unchanged for any scale. The int() cast truncates toward zero (no rounding), so
a deflection scaled past the isoline is pulled back toward it rather than away.

Only `points:` lines are touched. Headers, `count:`, `markers:`, `elements:`
(amplitudes there are in mV, a separate domain), `manifest.txt` and `groups.txt`
are copied through byte-for-byte, and entry names/order are preserved.

Output text is UTF-8 *without a BOM* (a BOM breaks the app's first-line parse)
with LF line endings, matching the rest of the dataset.

Inputs
  --in           source Pathologies.zip          (default: the repo asset)
  --out          destination zip                 (default: <in stem>.scaled.zip)
  --scale        amplitude multiplier (>0; <1 attenuates, >1 amplifies)
  --baseline     ADC isoline to scale around (default: 1024)
  --ids          comma-separated pathology ids to limit scaling to (default: all)
  --clamp-min    floor applied to every scaled sample (default: none)
  --clamp-max    ceiling applied to every scaled sample (default: none)
  --dry-run      report what would change, but do not write the zip

Examples
  # make every waveform 50% taller (default in/out)
  python scale_amplitude.py --scale 1.5

  # halve amplitude of two pathologies only, into a named output
  python scale_amplitude.py --in Pathologies.zip --scale 0.5 \\
      --ids anteriormi,3abblock --out Pathologies.low.zip

  # preview the effect without writing
  python scale_amplitude.py --in Pathologies.zip --scale 2.0 --dry-run
"""
import argparse
import os
import sys
import zipfile

DEFAULT_BASELINE = 1024

DEFAULT_IN = os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", "..", "src", "CardioSimulator.App", "Assets", "Pathologies.zip"))


def scale_points_value(value_field, scale, baseline, clamp_min, clamp_max):
    """Scale one CSV `points:` payload; return (new_text, count, min, max)."""
    out = []
    lo = hi = None
    for tok in value_field.split(","):
        tok = tok.strip()
        if not tok:
            continue
        v = int(tok)
        n = int(baseline + (v - baseline) * scale)
        if clamp_min is not None and n < clamp_min:
            n = clamp_min
        if clamp_max is not None and n > clamp_max:
            n = clamp_max
        out.append(n)
        lo = n if lo is None or n < lo else lo
        hi = n if hi is None or n > hi else hi
    return ",".join(str(x) for x in out), len(out), lo, hi


def scale_dat_text(text, scale, baseline, clamp_min, clamp_max):
    """Rewrite every `points:` line in a .dat; leave all other lines intact.

    Returns (new_text, samples_scaled, min_sample, max_sample). Works line by
    line so header order, blank-line block separators, `count:`, `markers:` and
    `elements:` are preserved exactly.
    """
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    new_lines = []
    samples = 0
    lo = hi = None
    for line in text.split("\n"):
        if line.startswith("points:"):
            payload = line[len("points:"):]
            scaled, n, l, h = scale_points_value(
                payload, scale, baseline, clamp_min, clamp_max)
            new_lines.append("points:" + scaled)
            samples += n
            if l is not None:
                lo = l if lo is None or l < lo else lo
                hi = h if hi is None or h > hi else hi
        else:
            new_lines.append(line)
    return "\n".join(new_lines), samples, lo, hi


def main(argv=None):
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(
        description="Rescale ECG amplitude in a CardioSimulator Pathologies.zip.")
    ap.add_argument("--in", dest="inp", default=DEFAULT_IN, help="source Pathologies.zip")
    ap.add_argument("--out", dest="out", help="destination zip (default: <in stem>.scaled.zip)")
    ap.add_argument("--scale", type=float, required=True, help="amplitude multiplier (>0)")
    ap.add_argument("--baseline", type=int, default=DEFAULT_BASELINE,
                    help=f"ADC isoline to scale around (default: {DEFAULT_BASELINE})")
    ap.add_argument("--ids", help="comma-separated pathology ids to limit scaling to")
    ap.add_argument("--clamp-min", type=int, help="floor for every scaled sample")
    ap.add_argument("--clamp-max", type=int, help="ceiling for every scaled sample")
    ap.add_argument("--dry-run", action="store_true",
                    help="report changes but do not write the zip")
    args = ap.parse_args(argv)

    if not os.path.isfile(args.inp):
        print(f"ERROR: source zip not found: {args.inp}", file=sys.stderr)
        return 2
    if args.scale <= 0:
        print("ERROR: scale must be > 0", file=sys.stderr)
        return 2
    if (args.clamp_min is not None and args.clamp_max is not None
            and args.clamp_min > args.clamp_max):
        print("ERROR: --clamp-min must be <= --clamp-max", file=sys.stderr)
        return 2

    out = args.out or (os.path.splitext(args.inp)[0] + ".scaled.zip")
    if not args.dry_run and os.path.abspath(out) == os.path.abspath(args.inp):
        print("ERROR: --out must differ from --in", file=sys.stderr)
        return 2

    only = None
    if args.ids:
        only = {s.strip() for s in args.ids.split(",") if s.strip()}

    # Read every entry up front (preserving names/order) so the output is a
    # faithful copy with only the targeted .dat payloads changed.
    with zipfile.ZipFile(args.inp) as zin:
        infos = zin.infolist()
        entries = [(info.filename, zin.read(info.filename)) for info in infos]

    dat_total = 0
    dat_scaled = 0
    samples_total = 0
    overall_lo = overall_hi = None
    new_entries = []
    matched_ids = set()

    for name, data in entries:
        base = os.path.basename(name)
        if base.endswith(".dat"):
            dat_total += 1
            pid = base[:-4]
            if only is not None and pid not in only:
                new_entries.append((name, data))
                continue
            matched_ids.add(pid)
            text = data.decode("utf-8")
            new_text, n, lo, hi = scale_dat_text(
                text, args.scale, args.baseline, args.clamp_min, args.clamp_max)
            dat_scaled += 1
            samples_total += n
            if lo is not None:
                overall_lo = lo if overall_lo is None or lo < overall_lo else overall_lo
                overall_hi = hi if overall_hi is None or hi > overall_hi else overall_hi
            new_entries.append((name, new_text.encode("utf-8")))
        else:
            new_entries.append((name, data))

    if only is not None:
        missing = sorted(only - matched_ids)
        if missing:
            print("WARNING: --ids not found as .dat:", ", ".join(missing),
                  file=sys.stderr)

    print(f"pathologies (.dat) : {dat_total}")
    print(f"scaled             : {dat_scaled} (scale={args.scale}, baseline={args.baseline})")
    print(f"samples rescaled   : {samples_total:,}")
    if overall_lo is not None:
        print(f"sample range after : [{overall_lo}, {overall_hi}]")

    if dat_scaled == 0:
        print("Nothing to scale.", file=sys.stderr)
        return 1

    if args.dry_run:
        print(f"\nDry run: would write {out}")
        return 0

    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zout:
        for name, data in new_entries:
            zout.writestr(name, data)

    print(f"\nwrote {out} ({os.path.getsize(out):,} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
