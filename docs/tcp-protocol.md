# CardioSimulator TCP streaming protocol

> Wire contract between the app and an external **monitor server** on the LAN. The app opens the
> connection, pushes the rhythm catalog once, and then — **on each rhythm the user selects** — asks whether
> the server already has that rhythm and, only if it doesn't, sends the whole rhythm's raw samples in **one
> message**. The **start** command (play) is separate and goes when the user presses the start button — or, while a
> rhythm is playing, as **`stop` → `start`** after the selection's `query`, so the server switches to the new rhythm.
>
> Все **технические идентификаторы** (`type`, ключи JSON, токены отведений, `OK`/`no_data`) — латиницей и
> **точь-в-точь**: обе стороны парсят их буквально. Краткая сводка для разработчика сервера — в конце (§9).

Canonical implementation:
[`AppViewModel.cs`](../src/CardioSimulator.App/ViewModels/AppViewModel.cs) (client),
[`TcpMessage.cs`](../src/CardioSimulator.Core/Network/TcpMessage.cs) +
[`TcpProtocol.cs`](../src/CardioSimulator.Core/Network/TcpProtocol.cs) (encode/decode).

---

## 1. Transport & framing

| | |
|---|---|
| Transport | Raw **TCP**, one long-lived connection. **No TLS** — confidentiality is the network's job. |
| Roles | The **app is the client**; it connects out to a user-configured `IP:port` (default port `8080`, set in Settings). The **server listens**. |
| Framing | **Line-delimited JSON**: one compact JSON object per line, UTF-8, terminated by a single `\n` (`0x0A`). No `\r` is required; the app trims it if present. |
| Exception | The **`upload`** message is followed by a raw binary payload that is *not* line-delimited — see §5. |
| Reconnect | The app auto-reconnects every ~5 s. Each new connection repeats the whole lifecycle (§2) from the top. |

A `rhythm` message (§3.3) can be a **large single line** (all leads of a record) — the server must be able to
read a long line, not assume a small fixed buffer. Replies from the server are always short.

---

## 2. Connection lifecycle

```
app ── connect ──────────────────────────────►  server
app ── time   (client's system date/time) ───►          (§3.7 — no reply)
app ── upload manifest.txt (§5) ─────────────►          (catalog: every rhythm's id/title/metadata)
app ── query  (currently-selected rhythm) ───►          (the app always points at some rhythm — push it now)
app ◄─ "OK" | "no_data" ──────────────────────  server
app ── rhythm (…) ───────────────────────────►          (only if "no_data")

        · · · user SELECTS rhythm A · · ·
app ── query  (pathology=A, hash) ───────────►
app ◄─ "OK" | "no_data" ──────────────────────  server   (cache verdict, §4)
app ── rhythm (A, all leads, raw samples) ───►          (ONLY if "no_data"; one message, not a stream)

        · · · user clicks START · · ·
app ── start  (pathology=A) ─────────────────►          (play command — no data)

        · · · while PLAYING, user selects rhythm B · · ·
                                                        (the app keeps drawing A throughout)
app ── query  (pathology=B, revision, hash) ─►
app ◄─ "OK" | "no_data" ──────────────────────  server
app ── rhythm (B, …) ────────────────────────►          (ONLY if "no_data")
app ── stop ─────────────────────────────────►          (stop playing A)
app ── start  (pathology=B) ─────────────────►          (play B — and only now does the app draw B)

        · · · user clicks STOP · · ·
app ── stop ─────────────────────────────────►
```

On connect the app immediately runs the selection handshake for **whatever rhythm is currently selected** (it
always has one), right after the manifest — so the server is showing the current rhythm before the user touches
anything. The same happens again on every reconnect.

**The app follows the server, not the other way round.** While a rhythm is playing, selecting another one does
not change what the app draws: it keeps rendering the current rhythm until the exchange above has finished, and
switches its own trace at the same moment it sends `start`. While the monitor is **stopped** the app draws the
newly selected rhythm at once, but its **start button stays disabled** until the rhythm has reached the server —
so playback can never begin on a rhythm the server has not got. A send that fails, is superseded or is cancelled
releases both (the app falls back to drawing the selection after 15 s, so a wedged socket cannot freeze the UI).

Three things that changed from the earlier revision, at the customer's request:
- **`start` is the play command only** — sent on the start button, never on a selection made while stopped.
  A selection made **while the rhythm is playing** switches playback: `query` → (`rhythm` on `no_data`) → `stop` →
  `start`. The `stop`/`start` pair waits for the cache verdict (and the `rhythm`, if any), so `start` always follows
  data the server has.
- **A `revision` travels with the rhythm** (§6): `"0"` for the rhythm exactly as the dataset shipped it, a short
  fingerprint once an instructor has edited it. The `query` and the `rhythm` that answers it always carry the same
  value.
- **Data is one message, not a stream.** On selection the app sends the rhythm's samples as a single `rhythm`
  message carrying every lead, using the **raw values from the `.dat` file** (ADC integers) — not the old
  chunked `points` frames, and not baseline-zeroed floats.

---

## 3. Message reference

Every message is a JSON object with a `type` field and an optional `id` (a UUID used to correlate a reply
to its request — echoed back verbatim when the server chooses to; see §4). Fields shown as *(optional)* are
**omitted entirely** when empty/default rather than sent as `null`.

### 3.1 `query` — app → server  *(the cache probe)*

Sent when the user selects a rhythm: "do you already have this rhythm's data?".

```json
{"type":"query","id":"1f2e…","pathology":"ecg42200","revision":"0","hash":"3af9c1e0b2d4e5f6"}
```

| Field | Type | Notes |
|---|---|---|
| `pathology` | string | Rhythm id — the same id used in `manifest.txt`. Primary cache key. |
| `revision` | string *(optional)* | Content revision of this rhythm on this install (§6). `"0"` = as shipped. |
| `hash` | string *(optional)* | Content fingerprint of the samples (§6). Second half of the cache key. |

### 3.2 server reply — server → app

One line per `query` (§4). Either a **bare token** or a **JSON object**:

```
OK
no_data
```
```json
{"id":"1f2e…","status":"ok"}
{"id":"1f2e…","status":"no_data"}
```

### 3.3 `rhythm` — app → server  *(the whole rhythm, one message)*

Sent **only** after a `no_data`. Carries every stored lead's **raw `.dat` samples** at once.

```json
{"type":"rhythm","id":"a7…","pathology":"ecg42200","revision":"abc123ef","sampleRate":500,
 "leads":{"I":[1024,1000,…],"II":[1030,1028,…],"V3":[959,954,…]}}
```

| Field | Type | Notes |
|---|---|---|
| `pathology` | string | The rhythm id these samples belong to (matches the `query`). |
| `revision` | string *(optional)* | Exactly the value the matching `query` carried (§6) — store the samples under it. |
| `sampleRate` | int *(optional)* | Samples per second (default 500). |
| `leads` | object | Map of **lead token → integer array**. Tokens: `I II III aVR aVL aVF V1 V2 V3 V4 V5 V6`. |

Values are the **raw ADC integers stored in the `.dat`** — baseline-centered on **1024** (a flat baseline reads
~1024, not 0). Only the leads actually present in the file are sent (typically the independent leads; the server
may derive the rest). This is the same data an instructor sees, overlay-merged, so it reflects their edits.

### 3.4 `start` — app → server  *(play command)*

Sent when the user presses the **start** button. Tells the server to begin showing the already-delivered rhythm.
Also sent — right after a `stop` — when the user selects another rhythm while playing (§2), so the server switches
from the previous rhythm to the new one.

```json
{"type":"start","id":"…","sampleRate":500,"params":{"pathology":"ecg42200","name":"Sinus rhythm"}}
```

`params.pathology` identifies which rhythm to play; `params.name` is a display title. (`start` keeps the
`params` object for backward compatibility; the newer `query`/`rhythm` messages put `pathology` at top level.)
The server acknowledges `start` and `rhythm` messages with `OK`, `ack`, or `{"id":"…","status":"ok"}`. The app awaits this confirmation (with a 4 s fail-open timeout) while displaying a modal waiting dialog.

### 3.5 `stop` — app → server

```json
{"type":"stop","id":"…"}
```

Sent when the monitor is stopped, and before the `start` that switches playback to a newly selected rhythm (§2).
Carries no data.

### 3.6 `upload` — app → server  *(header for a binary payload, §5)*

```json
{"type":"upload","id":"…","filename":"manifest.txt","size":8123}
```

| Field | Type | Notes |
|---|---|---|
| `filename` | string | Currently always `manifest.txt`. |
| `size` | long | Exact byte count of the raw payload that follows the header line. |

### 3.7 `time` — app → server  *(the client's clock)*

Sent **once per connection**, as the very first line, before the catalog.

```json
{"type":"time","id":"…","datetime":"2026-09-17T13:35:12.345+03:00"}
```

| Field | Type | Notes |
|---|---|---|
| `datetime` | string | The app machine's local date/time, ISO-8601 with its UTC offset. |

Advisory, like `stop`: **do not reply to it.** An extra reply would be taken as the cache verdict for the first
`query` and desync the handshake (§4). It repeats on every reconnect, so the server always has a fresh stamp.

### 3.8 `ack` — server → app  *(optional)*

```json
{"type":"ack","id":"…","filename":"manifest.txt","bytes":8123}
```

For the server to acknowledge an `upload`. **The app ignores `ack` lines** — safe to send or omit.

> **Deprecated: `points`.** The old per-lead chunked-frame message is no longer sent by the app (the `rhythm`
> message replaces it). It remains defined in the protocol for compatibility but should not be expected.

---

## 4. The cache handshake

The point of the design: **don't re-send a rhythm the server already has.**

1. On each user selection the app sends **`query`** (§3.1) with `pathology` + `hash`.
2. The server looks up its cache and replies with **exactly one line**:
   - **`OK`** (or `{"status":"ok"}`) → it already holds this rhythm → the app sends **nothing** more.
   - **`no_data`** (or `{"status":"no_data"}`) → the app sends the single **`rhythm`** message (§3.3).

Rules the server must honor:

- **Newline-terminated.** Every reply ends with `\n`. Without it the app never sees the reply and falls back
  to the timeout below.
- **One reply per `query`, in socket order.** The app matches replies to requests **FIFO**. Do **not** reply
  `OK`/`no_data` to the `manifest.txt` upload (send an `ack` or nothing) — an extra early reply would be
  misattributed to the first rhythm.
- **Recommended: echo the `id`.** Replying `{"id":"<the query's id>","status":"ok"|"no_data"}` lets the app
  correlate by id, which makes rapid rhythm-switching and any interleaved messages unambiguous. The app accepts
  the bare token and the JSON form interchangeably, so this is a free upgrade at any time.
- **Timeout = 4 s, fail-open.** If no reply arrives within 4 seconds the app **sends the `rhythm` anyway**, so a
  silent or slow server never leaves the peer without a rhythm.

Rapid selection is safe: selecting another rhythm cancels the in-flight send, and the superseded `query`'s reply
is still consumed in order (as a discarded tombstone) so it can't desync the next one.

---

## 5. Binary payload framing (`upload`)

An `upload` header line is **immediately followed by exactly `size` raw bytes** of file content — not base64,
not line-delimited. The server must:

1. Read and parse the `upload` JSON line (up to and including its `\n`).
2. Read **exactly `size`** bytes — that is the file.
3. Resume line-delimited JSON parsing afterward.

Today the only upload is `manifest.txt`: the merged (overlay-applied) pathology catalog in the app's manifest
text format — one row per rhythm with id, title, lead count, group, clinical-case flag, number, etc. It is the
authoritative list of what rhythms exist; sample bodies arrive later, per selection, via the handshake.

---

## 6. Cache key, the content hash and `revision`

`hash` is a 64-bit **FNV-1a** fingerprint of the raw sample data, hex-encoded (16 chars), computed over every
lead's token and raw integer samples, leads visited in token order (stable regardless of map order).

**Why it matters for this app:** instructors *edit* rhythms. An edit keeps the pathology **id** but changes the
**samples**. If the server keys its cache by `pathology` **alone**, an edited rhythm would answer `OK` and show
the **stale** waveform. Therefore the server should key its cache by the pair **`(pathology, hash)`**: same id +
same hash → `OK`; same id + new hash → `no_data` (resend).

If the server prefers to ignore the hash and rely on a short-lived / per-session cache, it may — the field is
additive and safe to drop.

**`revision`** is the same idea in a form that is meaningful to a human reading a log: `"0"` means "this is the
rhythm exactly as the dataset shipped it", anything else means "an instructor on this machine created, imported or
edited it" and is a short fingerprint of that edited content. A `query` and the `rhythm` answering it always carry
the same value, so `(pathology, revision)` works as a cache key just as `(pathology, hash)` does.

Two properties worth knowing before relying on it:

- A revision is **per install**. Two machines that edit the same rhythm differently both stop reporting `"0"`, and
  their revisions differ because the content differs — but if an instructor **exports** an edited dataset and
  another machine loads that pack, its rhythms read `"0"` again there (they are that pack's shipped content). So
  `hash` remains the authoritative "is this the same waveform" answer; `revision` is the readable label.
- It moves with the **samples**. Renaming a rhythm or changing its tags does not change the waveform, so it does
  not change the revision.

---

## 7. Edge cases

| Situation | Behavior |
|---|---|
| Server never replies to a `query` | App waits 4 s, then sends the `rhythm` (fail-open). |
| TCP connects after a rhythm is already selected | Handled: the app pushes the currently-selected rhythm right after the manifest (§2), so `start` always follows data the server has. |
| Connection drops mid-send | App abandons the send; on reconnect it re-uploads the manifest and resumes on the next selection. |
| Server sends an unrecognized line | Ignored by the app. |
| Two rhythms selected in quick succession | Only the latest is sent; the earlier send is cancelled. Both `query`s still receive (and consume) their replies. While playing, only the latest selection's `stop` → `start` is sent. |
| Rhythm selected while playing | `query` → (`rhythm` on `no_data`) → `stop` → `start` for the new rhythm. The app keeps drawing the previous rhythm until the `start` goes out. Pressing STOP before the pair goes out cancels it; only the user's `stop` is sent, and the app then draws the selection. |
| Rhythm selected while stopped | `query` → (`rhythm` on `no_data`). The app draws the new rhythm at once, but its start button is disabled until the send settles, so `start` can't precede the data. |
| Server slow or silent mid-switch | The verdict fails open after 4 s and the `rhythm` is sent anyway. If the whole exchange has not settled within 15 s the app draws the selected rhythm regardless, so the UI can't be pinned to the old one by a wedged socket. |

---

## 8. Message summary

| `type` | Direction | Payload | Purpose |
|---|---|---|---|
| `time` | app → server | `datetime` | The client's system clock, first line of every connection. No reply. |
| `upload` | app → server | header + raw bytes | Push `manifest.txt` (catalog) on connect. |
| `query` | app → server | `pathology`, `revision`, `hash` | On selection: ask if the server has this rhythm. |
| `OK` / `no_data` | server → app | bare token or `{id,status}` | Cache verdict: has it / send it. |
| `rhythm` | app → server | `pathology`, `revision`, `sampleRate`, `leads{token:int[]}` | The whole rhythm's raw `.dat` samples, one message (sent only on `no_data`). |
| `start` | app → server | `sampleRate`, `params{pathology,name}` | Play command (start button; after `stop` on a selection while playing). |
| `stop` | app → server | — | Monitor stopped; also precedes `start` on a selection while playing. |
| `ack` | server → app | `filename`, `bytes` | Optional upload acknowledgment (app ignores it). |
| `points` | app → server | *(deprecated — no longer sent)* | Former streamed frames; replaced by `rhythm`. |

---

## 9. Для разработчика сервера (кратко)

1. Сервер **слушает** TCP-порт; приложение подключается само. Обмен — **JSON, по одной строке**, кодировка
   UTF-8, конец строки `\n`.
2. Первой строкой каждого подключения приходит **`time`** — системные дата/время машины, где работает
   приложение, в формате ISO-8601 со смещением:
   `{"type":"time","id":"…","datetime":"2026-09-17T13:35:12.345+03:00"}`.
   **Отвечать на неё не нужно** (лишний ответ будет принят за вердикт кэша для первого `query`).
   Затем приходит `upload` c `filename:"manifest.txt"` и `size`. После строки-заголовка
   идут **ровно `size` байт** файла (каталог всех ритмов). Ответьте `ack` или ничего — **не** `OK`/`no_data`.
3. Сразу после манифеста (и затем при каждом **выборе** ритма) приходит **`query`** с `pathology` (id),
   `revision` (версия содержимого: `"0"` — ритм как в поставке, иначе отпечаток отредактированной копии) и
   `hash` (отпечаток данных) — приложение всегда указывает на какой-то ритм и присылает текущий при
   подключении. Проверьте кэш по паре **`(pathology, hash)`** и ответьте **одной строкой**:
   - **`OK`** — ритм уже есть → приложение **ничего** больше не пришлёт;
   - **`no_data`** — ритма нет → приложение пришлёт **одно** сообщение `rhythm` со всеми отведениями и
     **исходными значениями из `.dat`** (целые числа ADC, базовая линия ≈ 1024). Это **не** поток.
     `revision` в `rhythm` — тот же, что и в `query`: под ним и следует сохранить данные в кэше.
4. Когда пользователь нажимает **«старт»**, приходит **`start`** (`params.pathology`) — команда «показывай/
   проигрывай этот ритм». Данные к этому моменту уже переданы на шаге 3.
   Если ритм **уже проигрывается** и пользователь выбирает другой, приходит последовательность
   **`query` → (`rhythm`, если ответили `no_data`) → `stop` → `start`** (`params.pathology` — новый ритм):
   остановите текущий ритм и запустите новый. **Приложение до этого момента продолжает рисовать старый
   ритм** и переключает свой экран одновременно с отправкой `start` — картинка на сервере и в программе
   меняется вместе. Если монитор **остановлен**, новый ритм на экране появляется сразу, но **кнопка «старт»
   заблокирована**, пока ритм не доедет до сервера.
5. **Один ответ на каждый `query`, по порядку**, обязательно с `\n` в конце. Если не ответить за 4 с,
   приложение пришлёт `rhythm` всё равно. **Рекомендуется** отвечать `{"id":"<id из query>","status":"ok"|"no_data"}`.
6. `hash` меняется, когда преподаватель **отредактировал** ритм (id при этом прежний). Ключ кэша по
   `(pathology, hash)` гарантирует, что отредактированный ритм придёт заново, а не покажется устаревшим.
