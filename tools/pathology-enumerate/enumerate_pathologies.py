#!/usr/bin/env python3
"""Enumerate and rename the pathologies in a CardioSimulator Pathologies.zip.

Every pathology gets a 1-based `number`, surfaced by the app as the clinical-case
number ("Clinical case №N" in the dashboard header and "{N} <title>" in the rhythm
list). Existing numbers are preserved; only `.dat` files that lack one are assigned
the next free number (pass --renumber to reassign every pathology from scratch).

Each pathology is then **renamed** to `ecg{number}` (configurable via --prefix), with
the number **zero-padded to a uniform width** so every id has the same digit count
(e.g. a 10000-record set yields `ecg00001` .. `ecg10000`). The width defaults to the
digit count of the largest number and can be raised with --width:

  * the `.dat` entry is renamed to `ecg{0-padded number}.dat`,
  * its header `pathology:` id becomes `ecg{0-padded number}`,
  * the matching `pathology:` line in manifest.txt is renamed and gains `;number:<n>`.

The `number:` field itself stays a plain integer (the app parses it as an int); only the
id/filename is zero-padded.

The id and the filename must stay in lockstep because the app locates a pathology's
file as `<id>.dat` (the manifest carries no separate filename). WARNING: this means
the ids change — any Course/Test data that references the old pathology ids must be
updated to the new `ecg{number}` ids.

Inputs:
  --in         source Pathologies.zip
  --out        destination zip (default: <in> with an .enumerated suffix)
  --start      first number to assign (default: 1)
  --prefix     id/filename prefix (default: ecg -> ecg{number}.dat)
  --renumber   ignore existing numbers and renumber everything in manifest order
  --map-out    path for the old-id -> new-id map (default: <out>.rename-map.tsv)

The map file (a TSV of `old_id<TAB>new_id<TAB>number`, one row per pathology) is
consumed by update_courses.py to re-point Course/Test data at the renamed ids.

Numbering order follows manifest.txt; any `.dat` not listed there is appended in
alphabetical order. All text is written UTF-8 *without a BOM* — a BOM breaks the
app's first-line `version:` parse. LF line endings are preserved.

Usage:
  python enumerate_pathologies.py --in Pathologies.zip --out Pathologies.new.zip
"""
import argparse
import os
import sys
import zipfile


def norm(b):
    """Decode bytes + normalize to LF line endings (matches the app's extractor)."""
    return b.decode("utf-8").replace("\r\n", "\n").replace("\r", "\n")


def strip_field(line, key):
    """Remove an existing ';key:value' field from a semicolon-joined line."""
    parts = line.split(";")
    kept = [parts[0]] + [p for p in parts[1:] if not p.strip().startswith(key + ":")]
    return ";".join(kept)


def to_int(value):
    try:
        return int(value.strip())
    except (ValueError, AttributeError):
        return None


def manifest_order(text):
    """Ordered list of pathology ids as they appear in manifest.txt."""
    ids = []
    for line in text.split("\n"):
        if line.startswith("pathology:") and not line.lstrip().startswith("#"):
            ids.append(line.split(";", 1)[0][len("pathology:"):].strip())
    return ids


def manifest_numbers(text):
    """id -> existing number parsed from the manifest ';number:' fields."""
    out = {}
    for line in text.split("\n"):
        if not line.startswith("pathology:") or line.lstrip().startswith("#"):
            continue
        pid = line.split(";", 1)[0][len("pathology:"):].strip()
        for field in line.split(";")[1:]:
            field = field.strip()
            if field.startswith("number:"):
                n = to_int(field[len("number:"):])
                if n is not None:
                    out[pid] = n
    return out


def set_manifest_ids_and_numbers(text, rename, numbers):
    """Rename each `pathology:<old>` line to its new id and (re)write `;number:<n>`.
    `rename` and `numbers` are both keyed by the *old* id."""
    out = []
    for line in text.split("\n"):
        if line.startswith("pathology:") and not line.lstrip().startswith("#"):
            head, sep, rest = line.partition(";")
            old_id = head[len("pathology:"):].strip()
            line = "pathology:" + rename.get(old_id, old_id) + sep + rest
            line = strip_field(line, "number")
            n = numbers.get(old_id)
            if n is not None:
                line = line + ";number:" + str(n)
        out.append(line)
    return "\n".join(out)


def dat_number(text):
    """Existing 'number:' from a .dat header (the key:value block before the first
    blank line), or None."""
    for line in text.split("\n"):
        if line.strip() == "":
            break  # end of header block
        if line.startswith("number:"):
            return to_int(line[len("number:"):])
    return None


def set_dat_header(text, new_id, num):
    """Rewrite the .dat header: set `pathology:<new_id>` and a single `number:<num>`
    line (inserted after `title:`, else after `pathology:`). Only the header block
    (before the first blank line) is touched. Header keys are order-independent, so
    the insert position is cosmetic only."""
    lines = [l for l in text.split("\n") if not l.startswith("number:")]
    insert_at = 1
    for i, l in enumerate(lines):
        if l.strip() == "":
            break  # stay within the header block
        if l.startswith("pathology:"):
            lines[i] = "pathology:" + new_id
        elif l.startswith("title:"):
            insert_at = i + 1
    lines.insert(insert_at, "number:" + str(num))
    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser(description="Enumerate + rename pathologies in a Pathologies.zip")
    ap.add_argument("--in", dest="inp", required=True, help="source Pathologies.zip")
    ap.add_argument("--out", dest="out", default=None,
                    help="destination zip (default: <in> with an .enumerated suffix)")
    ap.add_argument("--start", type=int, default=1, help="first number to assign (default: 1)")
    ap.add_argument("--prefix", default="ecg", help="id/filename prefix (default: ecg)")
    ap.add_argument("--width", type=int, default=0,
                    help="minimum zero-padded digit width for ids (default: auto = digits of the largest number)")
    ap.add_argument("--renumber", action="store_true",
                    help="ignore existing numbers and renumber everything in manifest order")
    ap.add_argument("--map-out", dest="map_out", default=None,
                    help="path for the old-id -> new-id map (default: <out>.rename-map.tsv)")
    args = ap.parse_args()

    # Default the output next to the input with an ".enumerated" suffix.
    if not args.out:
        base = args.inp[:-4] if args.inp.lower().endswith(".zip") else args.inp
        args.out = base + ".enumerated.zip"

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

    manifest_text = norm(entries["manifest.txt"])
    dat_bases = {b for b in entries if b.endswith(".dat")}
    dat_ids = {b[:-4] for b in dat_bases}

    # Existing numbers: prefer the .dat header, fall back to the manifest field.
    dat_nums = {b[:-4]: dat_number(norm(entries[b])) for b in dat_bases}
    man_nums = manifest_numbers(manifest_text)
    existing = {}
    if not args.renumber:
        for pid in dat_ids:
            n = dat_nums.get(pid)
            if n is None:
                n = man_nums.get(pid)
            if n is not None:
                existing[pid] = n

    # Assignment order: manifest order first, then any stray .dat alphabetically.
    ordered = [p for p in manifest_order(manifest_text) if p in dat_ids]
    ordered += sorted(dat_ids - set(ordered))

    numbers = dict(existing)
    used = set(numbers.values())
    counter = [args.start]

    def next_number():
        while counter[0] in used:
            counter[0] += 1
        n = counter[0]
        used.add(n)
        counter[0] += 1
        return n

    added = 0
    for pid in ordered:
        if pid not in numbers:
            numbers[pid] = next_number()
            added += 1

    # New id/filename per pathology. The number is zero-padded to a uniform width so
    # every id has the same digit count (ids stay unique because numbers are unique).
    width = max(len(str(max(numbers.values()))) if numbers else 1, args.width)
    rename = {pid: f"{args.prefix}{numbers[pid]:0{width}d}" for pid in dat_ids}

    # Build the output entry set fresh so renames never clobber a still-pending source.
    out_entries = {b: entries[b] for b in entries if not b.endswith(".dat")}
    out_entries["manifest.txt"] = set_manifest_ids_and_numbers(
        manifest_text, rename, numbers).encode("utf-8")
    for base in dat_bases:
        pid = base[:-4]
        new_id = rename[pid]
        out_entries[new_id + ".dat"] = set_dat_header(
            norm(entries[base]), new_id, numbers[pid]).encode("utf-8")

    with zipfile.ZipFile(args.out, "w", zipfile.ZIP_DEFLATED) as zout:
        for base in sorted(out_entries):
            zout.writestr(base, out_entries[base])

    # Write the old-id -> new-id map (complete: one row per pathology, ordered by number).
    map_path = args.map_out or (
        args.out[:-4] if args.out.lower().endswith(".zip") else args.out) + ".rename-map.tsv"
    with open(map_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("# CardioSimulator pathology rename map\n")
        f.write("# generated by enumerate_pathologies.py -- feed to update_courses.py\n")
        f.write("# old_id\tnew_id\tnumber\n")
        for pid in sorted(dat_ids, key=lambda p: numbers[p]):
            f.write(f"{pid}\t{rename[pid]}\t{numbers[pid]}\n")

    renamed = sum(1 for pid in dat_ids if rename[pid] != pid)
    print(f"pathologies (.dat) : {len(dat_ids)}")
    print(f"already numbered   : {len(dat_ids) - added}")
    print(f"newly numbered     : {added}")
    print(f"renamed ids        : {renamed}")
    for pid in ordered:
        if rename[pid] != pid:
            print(f"    {pid}  ->  {rename[pid]}")
    print("wrote", args.out)
    print("wrote", map_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
