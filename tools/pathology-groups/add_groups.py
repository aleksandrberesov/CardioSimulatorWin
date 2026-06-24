#!/usr/bin/env python3
"""Add group markers to every pathology in a CardioSimulator Pathologies.zip.

Inputs:
  --in      source Pathologies.zip
  --out     destination zip to write
  --map     pathology_id -> group_key list (TSV; default: ./pathology_groups.tsv)
  --groups  groups catalog (default: ./groups.txt)

The output zip is a faithful copy of the input in which:
  * every `pathology:` line in manifest.txt carries a `;group:<key>` field,
  * every `<id>.dat` header carries a `group:<key>` line,
  * the groups catalog is bundled as an independent `groups.txt` entry
    (the app reads this as its source list of groups).

All text is written UTF-8 *without a BOM* — a BOM breaks the app's first-line
`version:` parse. LF line endings are preserved.

Usage:
  python add_groups.py --in Pathologies.zip --out Pathologies.new.zip
"""
import argparse
import os
import sys
import zipfile


def read_mapping(path):
    """id -> group_key, ignoring blanks and #comments. Accepts tab or spaces."""
    mapping = {}
    with open(path, encoding="utf-8-sig") as f:
        for raw in f:
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            parts = line.replace("\t", " ").split()
            if len(parts) >= 2:
                mapping[parts[0]] = parts[1]
    return mapping


def read_group_keys(path):
    """Ordered group keys declared in groups.txt (lines starting with 'group:')."""
    keys = []
    with open(path, encoding="utf-8-sig") as f:
        for raw in f:
            line = raw.strip()
            if line.startswith("group:"):
                keys.append(line.split(";", 1)[0][len("group:"):].strip())
    return keys


def strip_field(line, key):
    """Remove an existing ';key:value' field from a semicolon-joined line."""
    parts = line.split(";")
    kept = [parts[0]] + [p for p in parts[1:] if not p.strip().startswith(key + ":")]
    return ";".join(kept)


def set_manifest_groups(text, mapping, missing):
    out = []
    for line in text.split("\n"):
        if line.startswith("pathology:") and not line.lstrip().startswith("#"):
            pid = line.split(";", 1)[0][len("pathology:"):].strip()
            line = strip_field(line, "group")
            grp = mapping.get(pid)
            if grp:
                line = line + ";group:" + grp
            else:
                missing.add(pid)
        out.append(line)
    return "\n".join(out)


def set_dat_group(text, grp):
    """Drop any existing 'group:' header line; insert the new one after 'pathology:'.

    Header keys are order-independent (everything before the first blank line is a
    key:value map), so inserting at line 1 is always valid.
    """
    lines = [l for l in text.split("\n") if not l.startswith("group:")]
    if grp and lines:
        lines.insert(1, "group:" + grp)
    return "\n".join(lines)


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    ap = argparse.ArgumentParser(description="Add group markers to a Pathologies.zip")
    ap.add_argument("--in", dest="inp", required=True, help="source Pathologies.zip")
    ap.add_argument("--out", dest="out", required=True, help="destination zip")
    ap.add_argument("--map", default=os.path.join(here, "pathology_groups.tsv"))
    ap.add_argument("--groups", default=os.path.join(here, "groups.txt"))
    args = ap.parse_args()

    mapping = read_mapping(args.map)
    group_keys = read_group_keys(args.groups)

    unknown = sorted({g for g in mapping.values() if g not in group_keys})
    if unknown:
        print("ERROR: mapping uses groups not declared in groups.txt:",
              ", ".join(unknown), file=sys.stderr)
        return 2

    with open(args.groups, encoding="utf-8-sig") as f:
        groups_text = f.read()

    # Read every entry by basename (flattens any nested dirs, like the app's extractor).
    with zipfile.ZipFile(args.inp) as zin:
        entries = {}
        for n in zin.namelist():
            base = os.path.basename(n)
            if base:
                entries[base] = zin.read(n)

    if "manifest.txt" not in entries:
        print("ERROR: manifest.txt not found in", args.inp, file=sys.stderr)
        return 2

    def norm(b):  # decode + normalize to LF line endings
        return b.decode("utf-8").replace("\r\n", "\n").replace("\r", "\n")

    missing = set()
    entries["manifest.txt"] = set_manifest_groups(
        norm(entries["manifest.txt"]), mapping, missing).encode("utf-8")

    dat_ids = set()
    for base in list(entries):
        if base.endswith(".dat"):
            pid = base[:-4]
            dat_ids.add(pid)
            entries[base] = set_dat_group(
                norm(entries[base]), mapping.get(pid)).encode("utf-8")

    # Bundle the groups catalog as an independent file (normalized to LF).
    entries["groups.txt"] = groups_text.replace("\r\n", "\n").replace("\r", "\n").encode("utf-8")

    with zipfile.ZipFile(args.out, "w", zipfile.ZIP_DEFLATED) as zout:
        for base in sorted(entries):
            zout.writestr(base, entries[base])

    print(f"pathologies (.dat) : {len(dat_ids)}")
    print(f"groups in catalog  : {len(group_keys)}")
    print(f"grouped entries    : {len(dat_ids) - len(missing & dat_ids)}")
    unmapped = sorted(dat_ids - set(mapping))
    if unmapped:
        print("WARNING: .dat with no mapping (left ungrouped):", ", ".join(unmapped))
    extra = sorted(set(mapping) - dat_ids)
    if extra:
        print("WARNING: mapping ids not present as .dat:", ", ".join(extra))
    print("wrote", args.out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
