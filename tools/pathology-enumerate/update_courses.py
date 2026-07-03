#!/usr/bin/env python3
"""Re-point a CardioSimulator Courses.zip at renamed pathology ids.

`enumerate_pathologies.py` renames every pathology to `ecg{number}` and writes a
rename map (TSV of `old_id<TAB>new_id`). Courses reference pathology ids in three
places, all rewritten here from that map:

  * courses manifest.txt  -> the `;pathologies:<csv>` field on each `course:` line,
  * <course>/course.txt    -> the `pathologies:<csv>` header line,
  * <course>/lectures/*.html -> the `pathology="<id>"` attribute of each <ecg> embed.

The zip's directory structure is preserved (courses are nested, unlike the flat
Pathologies.zip). All other entries (images, answers.json, etc.) are copied verbatim.
Remapping is a single pass over known old ids, so a rename cannot cascade
(e.g. a map of {aaa->ecg1, ecg1->ecg2} never turns aaa into ecg2).

Inputs:
  --in    source Courses.zip
  --out   destination zip to write
  --map   rename map produced by enumerate_pathologies.py (TSV: old_id  new_id)

All text is written UTF-8 *without a BOM* (a BOM breaks the app's first-line
`version:`/front-matter parse). LF line endings are preserved.

Usage:
  python update_courses.py --in Courses.zip --out Courses.new.zip --map Pathologies.new.rename-map.tsv
"""
import argparse
import os
import re
import sys
import zipfile


def norm(b):
    """Decode bytes + normalize to LF line endings (matches the app's extractor)."""
    return b.decode("utf-8").replace("\r\n", "\n").replace("\r", "\n")


def read_map(path):
    """old_id -> new_id, ignoring blanks and #comments. Columns are tab/space separated;
    only the first two are used (a trailing number column, if present, is ignored)."""
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


def remap_csv(value, mapping, stats):
    """Remap a comma-separated list of pathology ids; count hits and misses."""
    out = []
    for tok in value.split(","):
        pid = tok.strip()
        if not pid:
            continue
        if pid in mapping:
            out.append(mapping[pid])
            stats["refs"] += 1
        else:
            out.append(pid)
            stats["unknown"].add(pid)
    return ",".join(out)


def update_manifest(text, mapping, stats):
    """Rewrite the `;pathologies:<csv>` field on each `course:` line."""
    out = []
    for line in text.split("\n"):
        if line.startswith("course:") and not line.lstrip().startswith("#"):
            fields = line.split(";")
            for i, fld in enumerate(fields):
                if fld.strip().startswith("pathologies:"):
                    head, _, val = fld.partition(":")
                    fields[i] = head + ":" + remap_csv(val, mapping, stats)
            line = ";".join(fields)
        out.append(line)
    return "\n".join(out)


def update_course_txt(text, mapping, stats):
    """Rewrite the `pathologies:<csv>` header line (within the block before the first
    blank line)."""
    lines = text.split("\n")
    for i, line in enumerate(lines):
        if line.strip() == "":
            break  # end of header block
        if line.startswith("pathologies:"):
            lines[i] = "pathologies:" + remap_csv(line[len("pathologies:"):], mapping, stats)
    return "\n".join(lines)


def update_html(text, ecg_re, mapping, stats):
    """Rewrite the value of every `pathology="<old>"` attribute (covers <ecg> embeds and
    any data-pathology="..."). Single pass — no cascading renames."""
    def repl(m):
        stats["ecg"] += 1
        return m.group(1) + m.group(2) + mapping[m.group(3)] + m.group(2)
    return ecg_re.sub(repl, text)


def main():
    ap = argparse.ArgumentParser(description="Re-point a Courses.zip at renamed pathology ids")
    ap.add_argument("--in", dest="inp", required=True, help="source Courses.zip")
    ap.add_argument("--out", dest="out", required=True, help="destination zip")
    ap.add_argument("--map", required=True, help="rename map from enumerate_pathologies.py")
    args = ap.parse_args()

    mapping = read_map(args.map)
    if not mapping:
        print("ERROR: no mappings read from", args.map, file=sys.stderr)
        return 2

    # One alternation over known old ids, longest first so a shorter id can't shadow a
    # longer one sharing its prefix. The closing quote is a backreference to the opener.
    ids_alt = "|".join(re.escape(k) for k in sorted(mapping, key=len, reverse=True))
    ecg_re = re.compile(r'(pathology\s*=\s*)(["\'])(' + ids_alt + r')\2')

    stats = {"refs": 0, "ecg": 0, "unknown": set()}

    with zipfile.ZipFile(args.inp) as zin:
        items = [(e.filename, zin.read(e.filename)) for e in zin.infolist() if not e.is_dir()]

    out_items = []
    manifests = courses = htmls = 0
    for name, data in items:
        base = os.path.basename(name)
        if base == "manifest.txt":
            data = update_manifest(norm(data), mapping, stats).encode("utf-8")
            manifests += 1
        elif base == "course.txt":
            data = update_course_txt(norm(data), mapping, stats).encode("utf-8")
            courses += 1
        elif name.lower().endswith(".html"):
            data = update_html(norm(data), ecg_re, mapping, stats).encode("utf-8")
            htmls += 1
        out_items.append((name, data))

    with zipfile.ZipFile(args.out, "w", zipfile.ZIP_DEFLATED) as zout:
        for name, data in out_items:
            zout.writestr(name, data)

    print(f"map entries        : {len(mapping)}")
    print(f"manifest.txt        : {manifests}")
    print(f"course.txt files    : {courses}")
    print(f"lecture .html files : {htmls}")
    print(f"pathology refs remapped (manifest/course.txt): {stats['refs']}")
    print(f"<ecg> refs remapped (html)                   : {stats['ecg']}")
    if stats["unknown"]:
        print("WARNING: referenced ids not in the map (left unchanged):",
              ", ".join(sorted(stats["unknown"])))
    print("wrote", args.out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
