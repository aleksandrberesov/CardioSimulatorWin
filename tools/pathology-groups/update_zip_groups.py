#!/usr/bin/env python3
"""Update group markers in an existing CardioSimulator dataset ZIP by scanning the WFDB source headers.

Maintains existing group mappings for built-in pathologies from pathology_groups.tsv
while automatically resolving the groups for CPSC records (e.g. js00001, etc.) by
reading their source .hea files from the dataset directory.

Usage:
  python tools/pathology-groups/update_zip_groups.py \
      --in E:/VLN_Project/Data/Pathologies.All.corrected.zip \
      --out E:/VLN_Project/Data/Pathologies.All.grouped.zip
"""
import argparse
import csv
import os
import re
import sys
import zipfile

# Default paths
DEFAULT_DATASET = r"E:\VLN_Project\Data\a-large-scale-12-lead-electrocardiogram-database-for-arrhythmia-study-1.0.0"
DEFAULT_GROUPS_TXT = r"E:\VLN_Project\Data\groups.txt"
DEFAULT_PATHOLOGY_GROUPS_TSV = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "pathology_groups.tsv"
)

# Acronym mapping to group keys
ACRONYM_MAPPING = {
    # Sinus group
    "SB": "sinus",
    "SR": "sinus",
    "ST": "sinus",
    "SA": "sinus",
    "SAAWR": "sinus",
    
    # Arrhythmia group
    "ABI": "arrhythmia",
    "APB": "arrhythmia",
    "JEB": "arrhythmia",
    "JPT": "arrhythmia",
    "VB": "arrhythmia",
    "VEB": "arrhythmia",
    "VPB": "arrhythmia",
    "VET": "arrhythmia",
    "WAVN": "arrhythmia",
    "AFIB": "arrhythmia",
    "AF": "arrhythmia",
    "SVT": "arrhythmia",
    "AT": "arrhythmia",
    "AVNRT": "arrhythmia",
    "AVRT": "arrhythmia",
    
    # Conduction group
    "1AVB": "conduction",
    "2AVB": "conduction",
    "2AVB1": "conduction",
    "2AVB2": "conduction",
    "3AVB": "conduction",
    "AVB": "conduction",
    "IDC": "conduction",
    "IVB": "conduction",
    "LBBB": "conduction",
    "LBBBB": "conduction",
    "LFBBB": "conduction",
    "RBBB": "conduction",
    "PRIE": "conduction",
    
    # Hypertrophy group
    "LVH": "hypertrophy",
    "RAH": "hypertrophy",
    "RVH": "hypertrophy",
    
    # Ischemia group
    "STDD": "ischemia",
    "STE": "ischemia",
    "STTC": "ischemia",
    "STTU": "ischemia",
    "TWC": "ischemia",
    "TWO": "ischemia",
    
    # Infarction group
    "AQW": "infarction",
    "MI": "infarction",
    "MIBW": "infarction",
    "MIFW": "infarction",
    "MILW": "infarction",
    "MISW": "infarction",
    
    # Electrolyte group
    "UW": "electrolyte",
    
    # Syndromes group
    "ERV": "syndromes",
    "QTIE": "syndromes",
    "VPE": "syndromes",
    "WPW": "syndromes",
    
    # Special group
    "ALS": "special",
    "ARS": "special",
    "CCR": "special",
    "CR": "special",
    "FQRS": "special",
    "LVQRSAL": "special",
    "LVQRSCL": "special",
    "LVQRSLL": "special",
    "PWC": "special",
    "VFW": "special",
}

GROUP_PRIORITY = [
    "infarction",
    "ischemia",
    "electrolyte",
    "syndromes",
    "conduction",
    "arrhythmia",
    "hypertrophy",
    "sinus",
    "special",
]

def load_snomed_to_acronym(csv_path):
    snomed_to_acronym = {}
    with open(csv_path, "r", encoding="utf-8-sig") as f:
        reader = csv.reader(f)
        next(reader)  # skip header
        for row in reader:
            if not row:
                continue
            acronym, _, snomed = [val.strip() for val in row]
            acronym_clean = acronym.replace("\xa0", " ").strip()
            snomed_to_acronym[snomed] = acronym_clean
    return snomed_to_acronym

def get_best_group(dx_codes, snomed_to_acronym):
    matched_groups = set()
    for code in dx_codes:
        acro = snomed_to_acronym.get(code)
        if acro and acro in ACRONYM_MAPPING:
            matched_groups.add(ACRONYM_MAPPING[acro])
            
    if not matched_groups:
        return ""
        
    for g in GROUP_PRIORITY:
        if g in matched_groups:
            return g
            
    return next(iter(matched_groups))

def build_wfdb_groups_mapping(dataset_dir, snomed_csv):
    snomed_to_acronym = load_snomed_to_acronym(snomed_csv)
    wfdb_groups = {}
    
    print("Scanning .hea files in dataset to build group mapping...")
    wfdb_records_dir = os.path.join(dataset_dir, "WFDBRecords")
    for root, _, files in os.walk(wfdb_records_dir):
        for f in files:
            if f.lower().endswith(".hea"):
                full_path = os.path.join(root, f)
                # Pathology ID in the zip is the lowercased stem, e.g. "js00001"
                pid = os.path.splitext(f)[0].lower()
                
                # Parse .hea file for dx
                dx_codes = []
                with open(full_path, "r", encoding="utf-8", errors="replace") as heaf:
                    for line in heaf:
                        line = line.strip()
                        if line.startswith("#"):
                            m_dx = re.match(r"^#\s*dx:\s*(.*)$", line, re.IGNORECASE)
                            if m_dx:
                                dx_val = m_dx.group(1).strip()
                                dx_codes = [code.strip() for code in dx_val.split(",") if code.strip()]
                                break
                                
                group = get_best_group(dx_codes, snomed_to_acronym)
                if group:
                    wfdb_groups[pid] = group
                    
    return wfdb_groups

def read_built_in_mapping(path):
    """Load standard pathology_id -> group_key mapping from pathology_groups.tsv"""
    mapping = {}
    if os.path.isfile(path):
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
    keys = []
    with open(path, encoding="utf-8-sig") as f:
        for raw in f:
            line = raw.strip()
            if line.startswith("group:"):
                keys.append(line.split(";", 1)[0][len("group:"):].strip())
    return keys

def strip_field(line, key):
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
    lines = [l for l in text.split("\n") if not l.startswith("group:")]
    if grp and lines:
        # Insert group right after 'pathology:<id>' (usually line index 1)
        lines.insert(1, "group:" + grp)
    return "\n".join(lines)

def main():
    parser = argparse.ArgumentParser(description="Update group fields in an existing dataset ZIP.")
    parser.add_argument("--in", dest="inp", required=True, help="Path to input ZIP (e.g. Pathologies.All.corrected.zip)")
    parser.add_argument("--out", dest="out", required=True, help="Path to output ZIP")
    parser.add_argument("--dataset", default=DEFAULT_DATASET, help="Path to WFDB dataset root")
    parser.add_argument("--map", default=DEFAULT_PATHOLOGY_GROUPS_TSV, help="Path to built-in pathology_groups.tsv")
    parser.add_argument("--groups", default=DEFAULT_GROUPS_TXT, help="Path to groups.txt catalog")
    args = parser.parse_args()

    if not os.path.isfile(args.inp):
        print(f"ERROR: Input zip not found: {args.inp}", file=sys.stderr)
        return 2

    # 1. Load catalogs and mappings
    group_keys = read_group_keys(args.groups)
    built_in_mapping = read_built_in_mapping(args.map)
    
    snomed_csv = os.path.join(args.dataset, "ConditionNames_SNOMED-CT.csv")
    if not os.path.isfile(snomed_csv):
        print(f"ERROR: SNOMED-CT CSV not found at {snomed_csv}", file=sys.stderr)
        return 2
        
    wfdb_mapping = build_wfdb_groups_mapping(args.dataset, snomed_csv)
    print(f"Mapped {len(wfdb_mapping)} CPSC record IDs to group keys.")

    # Combine mappings (WFDB mapping takes precedence for CPSC records)
    combined_mapping = {**built_in_mapping, **wfdb_mapping}

    unknown = sorted({g for g in combined_mapping.values() if g not in group_keys})
    if unknown:
        print("ERROR: combined mapping uses groups not declared in groups.txt:",
              ", ".join(unknown), file=sys.stderr)
        return 2

    with open(args.groups, encoding="utf-8-sig") as f:
        groups_text = f.read()

    # 2. Read the ZIP file into memory
    print(f"Reading {args.inp}...")
    entries = {}
    with zipfile.ZipFile(args.inp) as zin:
        for n in zin.namelist():
            base = os.path.basename(n)
            if base:
                entries[base] = zin.read(n)

    if "manifest.txt" not in entries:
        print("ERROR: manifest.txt not found in input zip", file=sys.stderr)
        return 2

    def norm(b):
        return b.decode("utf-8").replace("\r\n", "\n").replace("\r", "\n")

    # 3. Update manifest
    missing = set()
    entries["manifest.txt"] = set_manifest_groups(
        norm(entries["manifest.txt"]), combined_mapping, missing
    ).encode("utf-8")

    # 4. Update each pathology .dat header
    dat_ids = set()
    print("Updating .dat headers...")
    for base in list(entries):
        if base.endswith(".dat"):
            pid = base[:-4]
            dat_ids.add(pid)
            grp = combined_mapping.get(pid)
            entries[base] = set_dat_group(
                norm(entries[base]), grp
            ).encode("utf-8")

    # 5. Add/overwrite groups.txt
    entries["groups.txt"] = groups_text.replace("\r\n", "\n").replace("\r", "\n").encode("utf-8")

    # 6. Write the output ZIP
    print(f"Writing to {args.out}...")
    with zipfile.ZipFile(args.out, "w", zipfile.ZIP_DEFLATED) as zout:
        for base in sorted(entries):
            zout.writestr(base, entries[base])

    # 7. Print summary statistics
    print("\nSummary:")
    print(f"  Pathologies (.dat) found  : {len(dat_ids)}")
    print(f"  Groups in catalog         : {len(group_keys)}")
    print(f"  Successfully grouped      : {len(dat_ids) - len(missing & dat_ids)}")
    unmapped = sorted(dat_ids - set(combined_mapping))
    if unmapped:
        print(f"  WARNING: {len(unmapped)} pathologies with no group mapping (left ungrouped).")
    print(f"Successfully wrote {args.out}!")
    return 0

if __name__ == "__main__":
    sys.exit(main())
