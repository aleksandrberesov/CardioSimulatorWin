#!/usr/bin/env python3
"""Resolve SNOMED CT diagnostic codes in CardioSimulator Pathologies.zip files.

Some pathology records contain unresolved numeric SNOMED CT indexes in their title,
name, and clinical_case fields (e.g. "Atrial Flutter + 55930002"). This script
takes a zip archive, replaces the unresolved codes with their clean human-readable
equivalents in manifest.txt and all individual .dat file headers, and writes a
corrected zip archive.

Inputs:
  --in       source Pathologies.zip
  --out      destination zip to write
"""

import argparse
import os
import re
import sys
import zipfile

# Dictionary mapping unresolved SNOMED CT codes to friendly names
SNOMED_MAP = {
    "55827005": "Left ventricular hypertrophy",
    "55930002": "ST segment changes",
    "10370003": "Artificial pacing rhythm",
    "713427006": "Complete right bundle branch block",
    "427172004": "Premature ventricular contractions",
    "61721007": "Counterclockwise rotation",
    "365413008": "R wave changes",
    "6374002": "Bundle branch block",
    "445118002": "Left anterior fascicular block",
    "713426002": "Incomplete right bundle branch block",
    "251223006": "Tall P wave",
    "106068003": "Atrial rhythm",
    "733534002": "Complete left bundle branch block",
    "29320008": "Ectopic rhythm",
    "425856008": "Paroxysmal ventricular tachycardia",
    "251205003": "Prolonged P wave",
    "81898007": "Ventricular escape rhythm",
    "251170000": "Blocked premature atrial contraction",
    "50799005": "Atrioventricular dissociation",
    "164896001": "Ventricular fibrillation",
    "54329005": "Acute anterior myocardial infarction",
    "57054005": "Acute myocardial infarction",
    "5609005": "Sinus arrest",
    "426648003": "Junctional tachycardia",
    "49578007": "Shortened PR interval",
    "251187003": "Atrial escape complex",
    "251166008": "AV nodal reentrant tachycardia",
    "233892002": "Ectopic atrial tachycardia",
    "61277005": "Accelerated idioventricular rhythm",
    "426664006": "Accelerated junctional rhythm",
    "63593006": "Supraventricular premature beats",
    "446813000": "Left atrial hypertrophy",
    "17366009": "Atrial arrhythmia",
    "426627000": "Bradycardia",
    "111288001": "Ventricular flutter",
    "426183003": "Mobitz type II AV block",
    "251120003": "Incomplete left bundle branch block",
    "65778007": "Sinoatrial block",
    "445211001": "Left posterior fascicular block",
    "418818005": "Brugada syndrome",
    "77867006": "Shortened QT interval"
}

# Regex to match codes as individual word boundaries
CODE_PATTERN = re.compile(r'\b(' + '|'.join(SNOMED_MAP.keys()) + r')\b')


def resolve_text(text):
    """Substitute matching SNOMED CT codes in the text with friendly names."""
    return CODE_PATTERN.sub(lambda m: SNOMED_MAP[m.group(1)], text)


def main():
    ap = argparse.ArgumentParser(
        description="Resolve numeric SNOMED CT diagnostic codes in CardioSimulator Pathologies.zip"
    )
    ap.add_argument("--in", dest="inp", required=True, help="source Pathologies.zip")
    ap.add_argument("--out", dest="out", required=True, help="destination fixed Pathologies.zip")
    args = ap.parse_args()

    if not os.path.exists(args.inp):
        print(f"ERROR: Input file does not exist: {args.inp}", file=sys.stderr)
        return 1

    print(f"Reading from: {args.inp}")
    print(f"Writing to:   {args.out}")

    try:
        with zipfile.ZipFile(args.inp, 'r') as zin:
            infolist = zin.infolist()
            total_files = len(infolist)
            
            with zipfile.ZipFile(args.out, 'w', compression=zipfile.ZIP_DEFLATED) as zout:
                for idx, item in enumerate(infolist, 1):
                    filename = item.filename
                    data = zin.read(filename)
                    
                    if filename == "manifest.txt" or filename.endswith(".dat"):
                        try:
                            text = data.decode('utf-8')
                            fixed_text = resolve_text(text)
                            data = fixed_text.encode('utf-8')
                        except UnicodeDecodeError:
                            # Fallback if there's any raw binary data issues
                            pass
                            
                    zout.writestr(filename, data)
                    
                    if idx % 5000 == 0 or idx == total_files:
                        print(f"Processed {idx}/{total_files} files...")
                        
        print("\nSuccessfully finished zip correction!")
        return 0
        
    except Exception as e:
        print(f"ERROR: Failed during processing: {e}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
