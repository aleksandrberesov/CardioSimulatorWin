# Sync: delta-binary (`CSD1`) `.dat` format → Android

**Status:** MOVED — the working plan now lives in the **Android** repo:
`E:\VLN_Project\CardioSimulator\docs\plans\active\2026-07-android-delta-binary-dat-format-parity.md`
(indexed under *Active* in that repo's `docs/plans/README.md`).

This stub is kept so the Windows-side sync trail isn't a dead end. Do not implement from an older
copy of this file: an early draft specified a **format version byte** after the `CSD1` magic that was
**removed** before the format shipped. Following it makes every real pack fail to parse.

## Authoritative format (matches shipped code)

Defined in `src/CardioSimulator.Core/Domain/PathologyParser.cs`
(`ParsePathology(byte[])` / `SerializePathologyBytes`). Little-endian; a *string* is
`int32 length` (−1 = null) + that many UTF-8 bytes:

```
[4]      magic 'C','S','D','1'          ← sole discriminator; no version field
string   header text block (the text serializer's pre-lead header, reused verbatim)
int32    lead count N
N ×      { uint8 lead index (enum ordinal); string elements text (nullable);
           int32 sampleCount; int16[sampleCount] delta samples }
```

Decode deltas with two's-complement wrap-around: `value = (short)(prev + delta)`. To change the
framing incompatibly, bump the magic (`CSD2`) — never add an in-band version.

## Windows-side outcome (context for the Android work)

- Dataset is **binary-first**: `ContentPacker binarize <master> <master>.bin` (one file at a time),
  then `pack-dir` builds each `*.pak` straight from it — the plaintext ZIP stage is retired.
  Driver: `build-pathology-packs.ps1`. See `README_BUILD.md` and
  `docs/plans/complete/2026-07-delta-binary-serialization.md`.
- Real results: 45,206 records, **12.40 GB → 5.42 GB** uncompressed; packs ~29 % smaller
  (All: 2,467 MB → 1,672 MB).
- **89 records (0.2 %) remain plain text** — the encoder refuses samples outside int16 (corrupt
  source spikes, e.g. `ecg37094` lead V5 at ±33 mV). So text and binary `.dat` coexist in one pack;
  any reader must sniff per file.
- Android has **no `.pak` reader** at all — the decoder in the Android plan is a prerequisite for
  that separate, larger port.
