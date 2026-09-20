# Plan: Port Group Test/Exam ECG Graphics Rendering to Android

**Created:** 2026-09-20  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In group testing and exam mode, students scan a QR code on their mobile devices to access the single-page web quiz. Previously, questions with ECG stimuli (`q.stimulus === 'ecg'`) displayed a placeholder text line reading *"ЭКГ показана на экране преподавателя."* instead of rendering the actual ECG graphics.

The Windows application now serves vector SVG ECG graphics directly to mobile clients via a dedicated `/api/ecg` HTTP endpoint. The mobile client renders each ECG image dynamically inside the question card using an `<img>` element pointing to `/api/ecg?token=<token>&qid=<qid>`, with an `<a>` link wrapping it so students can tap to view and pinch-to-zoom the high-resolution vector trace in a new tab.

This sync plan details porting this capability to the Android version of CardioSimulator so student devices connected to the Android group test server (NanoHTTPD) also display ECG graphics.

---

## 2. Part A: Mobile Quiz HTML Template Update (`GroupQuizPage`)

### Reference File (Windows):
- `E:\VLN_Project\CardioSimulator\Win\src\CardioSimulator.App\Network\GroupQuizPage.cs`

### Target File (Android):
- `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\network\GroupQuizPage.kt` (or equivalent location serving `GroupQuizPage.Html`)

### Instructions:
Update the `showQuiz()` JavaScript function in the HTML template string:
- For `q.stimulus === 'image'`, wrap the image in a link:
  `<a href="/api/image?token='+encodeURIComponent(token)+'&qid='+encodeURIComponent(q.id)+'" target="_blank"><img class="qimg" src="/api/image?token='+encodeURIComponent(token)+'&qid='+encodeURIComponent(q.id)+'" alt=""></a>`
- For `q.stimulus === 'ecg'`, replace the text note fallback with the image link:
  `<a href="/api/ecg?token='+encodeURIComponent(token)+'&qid='+encodeURIComponent(q.id)+'" target="_blank"><img class="qimg" src="/api/ecg?token='+encodeURIComponent(token)+'&qid='+encodeURIComponent(q.id)+'" alt="ЭКГ"></a>`

---

## 3. Part B: Server Endpoint & ECG Trace Rendering (`GroupTestServer` & `EcgSvgRenderer`)

### Reference Files (Windows):
- `E:\VLN_Project\CardioSimulator\Win\src\CardioSimulator.App\Network\GroupTestServer.cs`
- `E:\VLN_Project\CardioSimulator\Win\src\CardioSimulator.App\Rendering\EcgSvgRenderer.cs`

### Target Files (Android):
- `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\network\GroupTestServer.kt`
- `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\rendering\EcgSvgRenderer.kt`

### Instructions:
1. **Pass `PathologyRepository` to `GroupTestServer`**:
   Accept `PathologyRepository` (or waveform resolver) in `GroupTestServer`'s constructor.
2. **Expose Standalone SVG Generator in `EcgSvgRenderer`**:
   Add a public helper method `renderSvg(traces, scheme)` that returns the standalone `<svg ...>...</svg>` XML string (without `<figure>` tags) so it can be served as an image document.
3. **Implement `/api/ecg` Route in `GroupTestServer`**:
   - Intercept `GET /api/ecg?token=<token>&qid=<qid>`.
   - Verify participant `token` and retrieve question `qid`.
   - Verify `q.stimulus == QuestionStimulus.Ecg` and resolve `pathologyId` (falling back to `q.assemble?.sourcePathologyId` if applicable).
   - Resolve waveform points for the question's leads (`q.leadList`) via `PathologyRepository`.
   - Render SVG string via `EcgSvgRenderer.renderSvg(traces, q.scheme)`.
   - Return response with `200 OK`, `Content-Type: image/svg+xml; charset=utf-8`, and UTF-8 encoded SVG bytes.

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow:
1. Launch Group Exam / Group Test on an Android device running CardioSimulator.
2. Scan the generated QR code using a mobile phone on the same LAN.
3. Register student name and group.
4. Verify that questions with ECG stimuli display vector ECG grid graphics directly on the test page.
5. Tap on any ECG graphic and confirm it opens the full-screen vector SVG in a new browser tab with pinch-to-zoom support.
