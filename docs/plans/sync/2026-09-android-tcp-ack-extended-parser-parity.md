# Plan: Port TCP Extended ACK Parser & Uid Protocol Handling to Android

**Created:** 2026-09-18  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

The TCP protocol line-delimited JSON parser has been extended to support optional `uid` attributes across all message types, as well as extended `ACK` messages sent by the server carrying optional `status`, `size`, and nullable `filename` fields (e.g. `{"uid":null,"type":"ack","id":"...","filename":"manifest.txt","status":null,"size":119538}` and `{"uid":null,"type":"ack","id":"...","filename":null,"status":"no_data","size":0}`).

Previously, missing `filename` or `bytes` in `ack` frames caused deserialization exceptions, and `status: "no_data"` on an `ack` frame was ignored if `type: "ack"` took precedence.

This plan details porting these protocol extensions to the Android repository.

---

## 2. Part A: Protocol & Message Class Definition

1. Update `TcpMessage` / `AckMessage` data models in Kotlin:
   - Base `TcpMessage` class: Ensure `uid: String?` property is optional on all message models.
   - `AckMessage`:
     - Make `filename: String?` nullable and optional.
     - Add `status: String? = null`.
     - Support `size: Long? = null` and `bytes: Long? = null`, where `size` or `bytes` fallback to whichever numeric size was provided.

---

## 3. Part B: JSON Serializer & Deserializer Updates

1. In the JSON parser/decoder:
   - Extract optional `uid` string property from all incoming JSON frames.
   - For `ack` frames (`"type": "ack"`):
     - Extract `filename` (nullable string).
     - Extract `status` (nullable string).
     - Extract `size` or `bytes` as long values.
2. In the incoming line handler / query waiter classifier:
   - Check `status` ("ok", "no_data") prior to generic `type == "ack"` so an ACK containing `"status": "no_data"` correctly signals a cache miss (`needData = true`).

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Run unit tests on Android verifying parsing of `ack` frames:
   - `{"uid":null,"type":"ack","id":"2dbf9f20-c81b-4f5c-8328-15d204ee4cdc","filename":"manifest.txt","status":null,"size":119538}`
   - `{"uid":null,"type":"ack","id":"140cf9e1-931c-446d-b72b-a76f08eed3cd","filename":null,"status":"no_data","size":0}`
2. Verify that `ack` frames with `status: "no_data"` correctly trigger the fallback rhythm data push.
