#!/usr/bin/env python3
"""Add WFDB ECG records to a CardioSimulator Pathologies.zip.

This is the offline / batch counterpart of the app's "Import WFDB file…" button.
It reads PhysioNet WFDB records (`.hea` + `.mat`/`.dat`), rescales every 12-lead
signal into the app's native sample domain, and writes each record into the
bundled dataset zip as a new `<id>.dat` pathology plus a `manifest.txt` entry.

The conversion is a faithful port of the C# pipeline:
  WfdbHeaderParser  -> .hea record/signal/comment lines
  WfdbSignalCodec   -> formats 16, 61, 80, 212, 24, 32  + MATLAB v4 .mat
  WfdbConverter     -> mv = (raw - baseline) / gain ;
                       sample = round(1024 + mv * 256)   (baseline-centred,
                       256 ADC counts/mV; see EcgCalibration)
Signals whose description names a known lead (I, II, III, aVR, aVL, aVF,
V1..V6, case-insensitive) are kept, first-wins; anything else (PLETH, RESP…)
is skipped.

Output text is UTF-8 *without a BOM* (a BOM breaks the app's first-line parse)
with LF line endings, matching the rest of the dataset.

Inputs
  --in       source Pathologies.zip          (default: the repo asset)
  --out      destination zip                  (default: <in dir>/Pathologies.new.zip)
  --dataset  base dir for resolving record names (recursively searched)
  --records  TSV describing records + metadata (see wfdb_records.tsv)
  RECORD...  one or more .hea paths / record names (resolved via --dataset)

Per-record metadata (TSV columns, tab-separated, all but the first optional):
  record   <tab> id <tab> title <tab> name <tab> group
- record : path to a .hea (abs or relative to --dataset, extension optional),
           or a bare record name searched for under --dataset.
- id     : pathology id / .dat filename stem  (default: slug of the record name)
- title  : English title                       (default: the record name)
- name   : Russian name                        (default: empty)
- group  : group key from groups.txt           (default: --group, else none)

Examples
  # one record, default group
  python add_wfdb.py --dataset E:/VLN_Project/Data/010 JS00001 --group arrhythmia

  # a curated batch with nice titles/names
  python add_wfdb.py --dataset <chapman-root> --records wfdb_records.tsv \\
      --out E:/VLN_Project/Data/Pathologies.new.zip

  # preview only, no zip written
  python add_wfdb.py JS00001.hea --dry-run
"""
import argparse
import os
import re
import struct
import sys
import zipfile
from collections import OrderedDict

# ── app domain constants (mirror WfdbConverter / EcgCalibration) ──────────────
DOMAIN_BASELINE = 1024
DOMAIN_COUNTS_PER_MV = 256.0
DEFAULT_GAIN = 200.0          # WfdbConstants.DefaultGain (gain 0 => uncalibrated)
DEFAULT_FS = 250.0            # WfdbConstants.DefaultSamplingFrequency

LEAD_ORDER = ["I", "II", "III", "aVR", "aVL", "aVF",
              "V1", "V2", "V3", "V4", "V5", "V6"]
LEAD_LOOKUP = {l.lower(): l for l in LEAD_ORDER}

DEFAULT_IN = os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", "..", "src", "CardioSimulator.App", "Assets", "Pathologies.zip"))

# ── WFDB header parsing (port of WfdbHeaderParser) ────────────────────────────
FORMAT_RE = re.compile(r"^(\d+)(?:x(\d+))?(?::(\d+))?(?:\+(\d+))?$")
GAIN_RE = re.compile(r"^([-+]?[0-9]*\.?[0-9]+)(?:\(([-+]?\d+)\))?(?:/(.+))?$")


class WfdbError(Exception):
    pass


def parse_signal(line):
    toks = line.split(None, 8)  # description (last field) may contain spaces
    if len(toks) < 2:
        raise WfdbError(f"invalid signal line: {line!r}")

    m = FORMAT_RE.match(toks[1])
    if not m:
        raise WfdbError(f"invalid format spec: {toks[1]!r}")

    sig = {
        "filename": toks[0],
        "fmt": int(m.group(1)),
        "offset": int(m.group(4) or 0),
        "gain": DEFAULT_GAIN,
        "baseline": 0,
        "baseline_specified": False,
        "units": "mV",
        "adcres": 16,
        "adczero": 0,
        "initval": 0,
        "checksum": 0,
        "desc": "",
    }

    if len(toks) >= 3:
        gm = GAIN_RE.match(toks[2])
        if gm:
            sig["gain"] = float(gm.group(1))
            if gm.group(2) is not None:
                sig["baseline"] = int(gm.group(2))
                sig["baseline_specified"] = True
            sig["units"] = (gm.group(3) or "mV").strip()
        else:  # just units, no gain => uncalibrated
            sig["gain"] = 0.0
            sig["units"] = toks[2]

    def as_int(tok):
        try:
            return int(tok)
        except ValueError:
            return None

    if len(toks) >= 4 and as_int(toks[3]) is not None:
        sig["adcres"] = as_int(toks[3])
    if len(toks) >= 5 and as_int(toks[4]) is not None:
        sig["adczero"] = as_int(toks[4])
    if len(toks) >= 6 and as_int(toks[5]) is not None:
        sig["initval"] = as_int(toks[5])
    if len(toks) >= 7 and as_int(toks[6]) is not None:
        sig["checksum"] = as_int(toks[6])
    if len(toks) >= 9:
        sig["desc"] = toks[8].strip()

    # When no explicit baseline was given, WFDB uses the ADC zero as the baseline.
    if not sig["baseline_specified"]:
        sig["baseline"] = sig["adczero"]
    return sig


def parse_header(text):
    lines = text.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    record = None
    sig_lines = []
    comments = []
    for raw in lines:
        line = raw.rstrip()
        if not line:
            continue
        if line.startswith("#"):
            comments.append(line[1:].lstrip())
            continue
        if record is None:
            record = line
        else:
            sig_lines.append(line)
    if record is None:
        raise WfdbError("header has no record line")

    toks = record.split()
    if len(toks) < 2:
        raise WfdbError(f"invalid record line: {record!r}")
    name = toks[0].split("/", 1)[0]
    nsig = int(toks[1])
    fs = DEFAULT_FS
    if len(toks) >= 3:
        try:
            fs = float(toks[2].split("/", 1)[0])
        except ValueError:
            pass
    nsamp = 0
    if len(toks) >= 4:
        try:
            nsamp = int(toks[3])
        except ValueError:
            nsamp = 0

    count = min(nsig, len(sig_lines)) if nsig > 0 else len(sig_lines)
    signals = [parse_signal(sig_lines[i]) for i in range(count)]
    return {"name": name, "nsig": nsig, "fs": fs, "nsamp": nsamp,
            "signals": signals, "comments": comments}


# ── sample decoding (port of WfdbSignalCodec + MatlabLevel4) ──────────────────
def _reshape(flat, channels):
    """Frame-interleaved flat stream -> result[channel] (slice with step)."""
    return [list(flat[c::channels]) for c in range(channels)]


def decode_flat(fmt, data, total):
    if fmt in (16, 61):
        need = total * 2
        if len(data) < need:
            raise WfdbError(f"format {fmt} data too short: need {need}, have {len(data)}")
        return list(struct.unpack(("<" if fmt == 16 else ">") + f"{total}h", data[:need]))
    if fmt == 32:
        need = total * 4
        if len(data) < need:
            raise WfdbError(f"format 32 data too short: need {need}, have {len(data)}")
        return list(struct.unpack(f"<{total}i", data[:need]))
    if fmt == 24:
        if len(data) < total * 3:
            raise WfdbError(f"format 24 data too short: need {total * 3}, have {len(data)}")
        out = []
        for i in range(total):
            b0, b1, b2 = data[i * 3], data[i * 3 + 1], data[i * 3 + 2]
            v = b0 | (b1 << 8) | (b2 << 16)
            if v & 0x800000:
                v -= 0x1000000
            out.append(v)
        return out
    if fmt == 80:
        if len(data) < total:
            raise WfdbError(f"format 80 data too short: need {total}, have {len(data)}")
        return [data[i] - 128 for i in range(total)]
    if fmt == 212:
        groups = (total + 1) // 2
        need = groups * 3 - (1 if total % 2 == 1 else 0)
        if len(data) < need:
            raise WfdbError(f"format 212 data too short: need {need}, have {len(data)}")
        out = []
        bp = 0
        i = 0
        while i < total:
            b0, b1 = data[bp], data[bp + 1]
            a = ((b1 & 0x0F) << 8) | b0
            if a & 0x800:
                a -= 0x1000
            out.append(a)
            i += 1
            if i < total:
                b2 = data[bp + 2]
                b = ((b1 & 0xF0) << 4) | b2
                if b & 0x800:
                    b -= 0x1000
                out.append(b)
                i += 1
                bp += 3
        return out
    raise WfdbError(f"unsupported WFDB read format: {fmt}")


def _infer_samples(fmt, data_bytes, channels):
    if data_bytes <= 0 or channels <= 0:
        return 0
    per = {16: 2, 61: 2, 24: 3, 32: 4, 80: 1}.get(fmt)
    if per is not None:
        return data_bytes // (per * channels)
    if fmt == 212:
        return data_bytes * 2 // (3 * channels)
    raise WfdbError(f"cannot infer sample count for format {fmt}; header must declare it")


def decode_dat(first, buf, channels, declared):
    fmt = first["fmt"]
    off = first["offset"]
    nsamp = declared if declared > 0 else _infer_samples(fmt, len(buf) - off, channels)
    total = channels * nsamp
    flat = decode_flat(fmt, buf[off:], total)
    return _reshape(flat, channels)


def _read_mat_data(seg, p, count):
    fmt = {0: "d", 1: "f", 2: "i", 3: "h", 4: "H", 5: "B"}.get(p)
    if fmt is None:
        raise WfdbError(f"unsupported MAT numeric class (P={p})")
    vals = struct.unpack(f"<{count}{fmt}", seg)
    if p in (0, 1):  # double / single -> truncate toward zero like the C# (int) cast
        return [int(v) for v in vals]
    return list(vals)


def decode_mat(buf, channels, declared):
    if len(buf) < 20:
        raise WfdbError("MAT file shorter than its 20-byte header")
    type_, rows, cols, imag, namelen = struct.unpack("<5i", buf[:20])
    m = type_ // 1000
    p = (type_ // 10) % 10
    t = type_ % 10
    if m != 0:
        raise WfdbError(f"unsupported MAT byte order (M={m}); only little-endian supported")
    if t != 0:
        raise WfdbError(f"unsupported MAT matrix type (T={t}); only full matrices supported")
    if rows < 0 or cols < 0 or namelen < 0:
        raise WfdbError("MAT file has invalid dimensions")

    data_start = 20 + namelen
    elem = {0: 8, 1: 4, 2: 4, 3: 2, 4: 2, 5: 1}.get(p)
    if elem is None:
        raise WfdbError(f"unsupported MAT numeric class (P={p})")
    count = rows * cols
    real = count * elem
    if data_start + real > len(buf):
        raise WfdbError("MAT data extends past end of buffer")

    data = _read_mat_data(buf[data_start:data_start + real], p, count)
    if rows != channels:
        raise WfdbError(f"MAT matrix has {rows} rows but header declares {channels} signal(s) in this file")
    if declared > 0 and cols != declared:
        raise WfdbError(f"MAT matrix has {cols} columns but header declares {declared} samples")
    # column-major [rows x cols] == frame-interleaved [channels x samples]
    return _reshape(data, rows)


def read_record(hea_path):
    """Read a full WFDB record: header + decoded raw ADC samples per signal."""
    with open(hea_path, "r", encoding="utf-8", errors="replace") as f:
        header = parse_header(f.read())
    # prefer the on-disk filename as the record name (headers occasionally disagree)
    stem = os.path.splitext(os.path.basename(hea_path))[0]
    if stem:
        header["name"] = stem

    directory = os.path.dirname(os.path.abspath(hea_path))
    nsig = len(header["signals"])
    samples = [None] * nsig

    by_file = OrderedDict()
    for i, s in enumerate(header["signals"]):
        by_file.setdefault(s["filename"], []).append(i)

    for fname, idxs in by_file.items():
        with open(os.path.join(directory, fname), "rb") as f:
            raw = f.read()
        channels = len(idxs)
        if fname.lower().endswith(".mat"):
            decoded = decode_mat(raw, channels, header["nsamp"])
        else:
            decoded = decode_dat(header["signals"][idxs[0]], raw, channels, header["nsamp"])
        for local, gi in enumerate(idxs):
            samples[gi] = decoded[local]

    header["samples"] = samples
    return header


# ── integrity check (matches the C# ground-truth test) ────────────────────────
def signed16(total):
    cs = total & 0xFFFF
    return cs - 0x10000 if cs >= 0x8000 else cs


def verify_record(header):
    """Return list of (signal_desc, problem) for initial-value / checksum mismatches."""
    problems = []
    for i, s in enumerate(header["signals"]):
        raw = header["samples"][i]
        if not raw:
            continue
        if header["nsamp"] > 0 and raw[0] != s["initval"]:
            problems.append((s["desc"] or f"#{i}",
                             f"initial value {raw[0]} != header {s['initval']}"))
        if signed16(sum(raw)) != s["checksum"]:
            problems.append((s["desc"] or f"#{i}",
                             f"checksum {signed16(sum(raw))} != header {s['checksum']}"))
    return problems


# ── WFDB record -> app lead streams (port of WfdbConverter.ToPathologyFile) ───
def to_lead_streams(header):
    leads = OrderedDict()
    sigs = header["signals"]
    samples = header["samples"]
    for i, s in enumerate(sigs):
        if i >= len(samples) or samples[i] is None:
            continue
        lead = LEAD_LOOKUP.get(s["desc"].strip().lower())
        if lead is None or lead in leads:  # first signal for a lead wins
            continue
        gain = s["gain"] if s["gain"] != 0 else DEFAULT_GAIN
        baseline = s["baseline"]
        leads[lead] = [
            round(DOMAIN_BASELINE + ((r - baseline) / gain) * DOMAIN_COUNTS_PER_MV)
            for r in samples[i]
        ]
    return leads


def build_dat_text(pid, title, name, group, leads):
    out = [f"pathology:{pid}"]
    if group:
        out.append(f"group:{group}")
    out.append(f"title:{title}")
    out.append(f"name:{name or ''}")
    out.append(f"leads:{len(leads)}")
    for lead in LEAD_ORDER:
        if lead not in leads:
            continue
        s = leads[lead]
        out.append("")
        out.append(f"lead:{lead}")
        out.append(f"count:{len(s)}")
        out.append("points:" + ",".join(str(x) for x in s))
    return "\n".join(out) + "\n"


# ── manifest editing ──────────────────────────────────────────────────────────
def _parse_semicolon(line):
    fields = {}
    for part in line.split(";"):
        if ":" in part:
            k, v = part.split(":", 1)
            fields[k.strip()] = v.strip()
    return fields


def manifest_entry_line(e):
    line = (f"pathology:{e['id']};leads:{e['leads_count']}"
            f";samples:{e['samples']};title:{e['title']}")
    if e.get("name"):
        line += ";name:" + e["name"]
    if e.get("group"):
        line += ";group:" + e["group"]
    return line


def update_manifest(text, new_entries):
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    lines = text.split("\n")
    blank = next((i for i, l in enumerate(lines) if l.strip() == ""), len(lines))
    header = lines[:blank]
    body = lines[blank:]
    while body and body[0].strip() == "":
        body.pop(0)
    while body and body[-1].strip() == "":
        body.pop()

    for e in new_entries:
        body.append(manifest_entry_line(e))

    # Recompute aggregate counts from every real entry line.
    entry_lines = [l for l in body if l.startswith("pathology:")]
    total_leads = 0
    total_samples = 0
    for l in entry_lines:
        fields = _parse_semicolon(l)
        total_leads += int(fields.get("leads", 0) or 0)
        total_samples += int(fields.get("samples", 0) or 0)

    def set_header(key, value):
        for i, l in enumerate(header):
            if l.startswith(key + ":"):
                header[i] = f"{key}:{value}"
                return
    set_header("pathologies", len(entry_lines))
    set_header("total_lead_streams", total_leads)
    set_header("total_samples", total_samples)

    return "\n".join(header + [""] + body) + "\n"


# ── record collection / resolution ────────────────────────────────────────────
def slugify(text):
    s = re.sub(r"[^a-z0-9]+", "-", text.strip().lower()).strip("-")
    return s or "record"


def build_dataset_index(dataset):
    """basename(without .hea) -> full path, for resolving bare record names."""
    index = {}
    for root, _dirs, files in os.walk(dataset):
        for fn in files:
            if fn.lower().endswith(".hea"):
                index.setdefault(os.path.splitext(fn)[0], os.path.join(root, fn))
    return index


def resolve_hea(record, dataset, index):
    cand = record if record.lower().endswith(".hea") else record + ".hea"
    if os.path.isfile(cand):
        return cand
    if dataset:
        p = os.path.join(dataset, cand)
        if os.path.isfile(p):
            return p
        stem = os.path.splitext(os.path.basename(record))[0]
        if index is None:
            index = build_dataset_index(dataset)
        if stem in index:
            return index[stem]
    return None


def read_records_tsv(path):
    rows = []
    with open(path, encoding="utf-8-sig") as f:
        for raw in f:
            line = raw.rstrip("\n").rstrip("\r")
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            cols = line.split("\t")
            rec = cols[0].strip()
            if not rec or rec.lower() in ("record", "hea", "path"):  # skip header row
                continue
            rows.append({
                "record": rec,
                "id": cols[1].strip() if len(cols) > 1 else "",
                "title": cols[2].strip() if len(cols) > 2 else "",
                "name": cols[3].strip() if len(cols) > 3 else "",
                "group": cols[4].strip() if len(cols) > 4 else "",
            })
    return rows


def read_zip_state(path):
    """Existing entries (by basename), pathology ids, and group keys."""
    entries = OrderedDict()
    with zipfile.ZipFile(path) as zin:
        for n in zin.namelist():
            base = os.path.basename(n)
            if base:
                entries[base] = zin.read(n)
    ids = {b[:-4] for b in entries if b.endswith(".dat")}
    group_keys = []
    if "groups.txt" in entries:
        for raw in entries["groups.txt"].decode("utf-8", "replace").split("\n"):
            line = raw.strip()
            if line.startswith("group:"):
                group_keys.append(line.split(";", 1)[0][len("group:"):].strip())
    return entries, ids, group_keys


# ── main ──────────────────────────────────────────────────────────────────────
def main(argv=None):
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(
        description="Add WFDB ECG records to a CardioSimulator Pathologies.zip.")
    ap.add_argument("records", nargs="*", help=".hea paths or record names")
    ap.add_argument("--in", dest="inp", default=DEFAULT_IN, help="source Pathologies.zip")
    ap.add_argument("--out", dest="out", help="destination zip (default: <in dir>/Pathologies.new.zip)")
    ap.add_argument("--dataset", help="base dir for resolving record names (searched recursively)")
    ap.add_argument("--records", dest="records_tsv", help="TSV of records + metadata")
    ap.add_argument("--all", action="store_true",
                    help="import EVERY .hea found under --dataset (huge; combine with --limit)")
    ap.add_argument("--group", help="default group key for records lacking one")
    ap.add_argument("--limit", type=int, help="cap the number of records imported")
    ap.add_argument("--no-verify", action="store_true",
                    help="skip the initial-value / checksum integrity check")
    ap.add_argument("--strict", action="store_true",
                    help="treat integrity-check or group-key problems as errors")
    ap.add_argument("--dry-run", action="store_true",
                    help="parse + convert + report, but do not write the zip")
    args = ap.parse_args(argv)

    if not os.path.isfile(args.inp):
        print(f"ERROR: source zip not found: {args.inp}", file=sys.stderr)
        return 2
    out = args.out or os.path.join(os.path.dirname(os.path.abspath(args.inp)), "Pathologies.new.zip")
    if os.path.abspath(out) == os.path.abspath(args.inp):
        print("ERROR: --out must differ from --in", file=sys.stderr)
        return 2

    if args.all and not args.dataset:
        print("ERROR: --all requires --dataset", file=sys.stderr)
        return 2

    index = build_dataset_index(args.dataset) if args.dataset else None

    # Collect the records to import (TSV first, then positionals, then --all).
    requested = []
    seen = set()
    if args.records_tsv:
        for row in read_records_tsv(args.records_tsv):
            requested.append(row)
            seen.add(os.path.splitext(os.path.basename(row["record"]))[0])
    for rec in args.records:
        requested.append({"record": rec, "id": "", "title": "", "name": "", "group": ""})
        seen.add(os.path.splitext(os.path.basename(rec))[0])
    if args.all:
        for stem in sorted(index):  # every .hea under --dataset, not already listed
            if stem not in seen:
                requested.append({"record": index[stem], "id": "", "title": "",
                                  "name": "", "group": ""})
    if args.limit is not None:
        requested = requested[:args.limit]
    if not requested:
        print("ERROR: no records given (pass .hea paths/names, --records TSV, or --all)",
              file=sys.stderr)
        return 2

    entries, existing_ids, group_keys = read_zip_state(args.inp)
    if "manifest.txt" not in entries:
        print(f"ERROR: manifest.txt not found in {args.inp}", file=sys.stderr)
        return 2
    used_ids = set(existing_ids)
    total = len(requested)
    if total > 1000:
        print(f"NOTE: importing {total} records — expect a large (multi-GB) zip and a "
              f"long run. Ctrl-C to abort.", file=sys.stderr)

    class StrictAbort(Exception):
        pass

    def process(sink):
        """Convert each requested record; call sink(filename, bytes) per record
        (sink=None for a dry run). Returns (new_entries, problems)."""
        new_entries = []
        problems = 0
        done = 0
        for req in requested:
            hea = resolve_hea(req["record"], args.dataset, index)
            if not hea:
                print(f"  SKIP {req['record']}: .hea not found", file=sys.stderr)
                problems += 1
                if args.strict:
                    raise StrictAbort(f"{req['record']}: .hea not found")
                continue
            try:
                header = read_record(hea)
            except (WfdbError, OSError, struct.error) as ex:
                print(f"  SKIP {req['record']}: {ex}", file=sys.stderr)
                problems += 1
                if args.strict:
                    raise StrictAbort(f"{req['record']}: {ex}")
                continue

            if not args.no_verify:
                for desc, msg in verify_record(header):
                    print(f"  WARN {header['name']} [{desc}]: {msg}", file=sys.stderr)
                    problems += 1
                    if args.strict:
                        raise StrictAbort(f"{header['name']} [{desc}]: {msg}")

            leads = to_lead_streams(header)
            if not leads:
                print(f"  SKIP {header['name']}: no recognised 12-lead signals", file=sys.stderr)
                problems += 1
                if args.strict:
                    raise StrictAbort(f"{header['name']}: no recognised 12-lead signals")
                continue

            # Resolve id (unique), title, name, group.
            base_id = slugify(req["id"]) if req["id"] else slugify(header["name"])
            pid = base_id
            n = 2
            while pid in used_ids:
                pid = f"{base_id}-{n}"
                n += 1
            used_ids.add(pid)

            title = req["title"] or header["name"]
            name = req["name"] or title  # never leave name empty; mirror the title
            group = req["group"] or args.group or ""
            if group and group_keys and group not in group_keys:
                print(f"  WARN {pid}: group '{group}' is not in groups.txt", file=sys.stderr)
                problems += 1
                if args.strict:
                    raise StrictAbort(f"{pid}: group '{group}' is not in groups.txt")

            total_samples = sum(len(v) for v in leads.values())
            if sink is not None:
                sink(f"{pid}.dat", build_dat_text(pid, title, name, group, leads).encode("utf-8"))
            new_entries.append({"id": pid, "title": title, "name": name, "group": group,
                                "leads_count": len(leads), "samples": total_samples})

            done += 1
            if total > 50:
                if done % 500 == 0:
                    print(f"  ... {done}/{total} (last: {pid})", file=sys.stderr)
            else:
                groups_note = f" group={group}" if group else ""
                print(f"  add {pid}: {len(leads)} leads, {total_samples} samples"
                      f" <- {os.path.basename(hea)}{groups_note}")
        return new_entries, problems

    if args.dry_run:
        try:
            new_entries, problems = process(None)
        except StrictAbort as ex:
            print(f"ERROR: --strict: {ex}", file=sys.stderr)
            return 2
        if not new_entries:
            print("Nothing to add.", file=sys.stderr)
            return 1
        print(f"\nDry run: would add {len(new_entries)} pathology(ies) to {out}")
        if problems:
            print(f"({problems} warning(s)/skip(s) — see above)")
        return 0

    # Stream into the output zip: existing entries (minus manifest) first, then
    # each converted .dat as it is produced, then the rebuilt manifest last. This
    # keeps memory flat regardless of how many records are imported.
    manifest_text = entries["manifest.txt"].decode("utf-8")
    new_entries = []
    problems = 0
    try:
        with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zout:
            for base, data in entries.items():
                if base != "manifest.txt":
                    zout.writestr(base, data)
            new_entries, problems = process(lambda fn, b: zout.writestr(fn, b))
            if not new_entries:
                raise StrictAbort("nothing to add")
            zout.writestr("manifest.txt",
                          update_manifest(manifest_text, new_entries).encode("utf-8"))
    except StrictAbort as ex:
        if os.path.exists(out):
            os.remove(out)
        print(f"ERROR: {ex}; removed partial {out}", file=sys.stderr)
        return 2

    print(f"\nAdded {len(new_entries)} pathology(ies); wrote {out} "
          f"({os.path.getsize(out):,} bytes)")
    if problems:
        print(f"({problems} warning(s)/skip(s) — see above)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
