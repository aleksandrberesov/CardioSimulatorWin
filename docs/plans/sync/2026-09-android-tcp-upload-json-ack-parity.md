# Plan: Port TCP JSON Upload/Ack Response Parsing Behavior to Android

**Created:** 2026-09-18  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

During live server communication over TCP, when the app uploads files (e.g., `manifest.txt`), servers may send JSON acknowledgment/response messages carrying `type: "upload"` or `type: "ack"`, along with optional `filename`, `size`, `bytes`, and `status` fields:
`{"uid":null,"type":"upload","id":"46befbbf-4d80-454c-8f5f-628822b1d21a","filename":"manifest.txt","status":null,"size":119538}`

Previously, TCP traffic logs and protocol decoders only checked for `type: "ack"` and strictly required both `filename` and `bytes` to be present, resulting in `type: "upload"` JSON lines being flagged as `unrecognized line (ignored by app)`.

The fix in `CardioSimulator.Core`:
1. Updated `TcpMessage.AckMessage` to make `filename`, `status`, `size`, and `bytes` optional fields.
2. Updated `TcpProtocol` serialization and deserialization to support `status` and handle both `size` and `bytes`.
3. Updated `TcpTrafficLog.ClassifyIncoming` and `AckSummary` to recognize `type: "upload"` (and JSON objects containing a `filename` property) as `ack` frames, extracting `size`/`bytes`/`status` cleanly into traffic log entries.

This plan details porting these TCP ACK and upload JSON response classification changes to the Android app.

---

## 2. Part A: Update TCP Ack Message Data Models & Decoder (Core Network Layer)

- **Target Kotlin Files/Classes:**
  - `TcpMessage.kt` (or `TcpMessage.AckMessage`)
  - `TcpProtocol.kt` (or parser/decoder equivalents)

- **Instructions:**
  1. Update `AckMessage` model in Kotlin:
     ```kotlin
     data class AckMessage(
         override val id: String? = null,
         override val uid: String? = null,
         val filename: String? = null,
         val status: String? = null,
         val size: Long? = null,
         val bytes: Long? = null
     ) : TcpMessage() {
         override val type: String = "ack"
     }
     ```
  2. In `TcpProtocol.fromJson` for `type == "ack"`:
     - Make `filename`, `status`, `size`, and `bytes` optional.
     - Fallback `size = optLong("size") ?: optLong("bytes")` and `bytes = optLong("bytes") ?: optLong("size")`.
  3. In `TcpProtocol.toJson` for `AckMessage`:
     - Conditionally serialize non-null properties: `filename`, `status`, `size`, `bytes`.

---

## 3. Part B: Update Traffic Log Classification for Upload Responses

- **Target Kotlin Files/Classes:**
  - `TcpTrafficLog.kt`

- **Instructions:**
  1. In `ClassifyIncoming` (or incoming line parser):
     - Check if JSON `type` is `"ack"` OR `"upload"`, or if JSON object has a `"filename"` property.
     - Classify these lines as `"ack"` traffic entries rather than `"unknown"`.
  2. Update `AckSummary`:
     - Format `filename` if present.
     - Format `status` if present and non-null.
     - Format `bytes` or `size` as `bytes=<count>` if present.

---

## 4. Part C: Verification

### 4.1 Unit Tests
1. Test parsing of `type: "ack"` JSON with missing `bytes` or using `size`.
2. Test parsing of `type: "upload"` JSON replies with `filename` and `size`.
3. Test traffic log classification of `{"uid":null,"type":"upload","id":"...","filename":"manifest.txt","status":null,"size":119538}`.

### 4.2 Manual Verification Flow
1. Connect Android app to TCP server.
2. Trigger manifest or data upload.
3. Verify in Server Traffic Log window that server reply `{"type":"upload", ...}` renders as:
   `ack manifest.txt bytes=119538`
   with kind `ack` instead of `unknown unrecognized line`.
