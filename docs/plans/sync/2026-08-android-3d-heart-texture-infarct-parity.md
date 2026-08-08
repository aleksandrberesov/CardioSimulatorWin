# Plan: Port 3D Heart Texture + Infarct Transition + Leads-Scheme Toggle to Android

**Created:** 2026-08-08
**Status:** NOT STARTED
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`

---

## 1. Background & Goals

A customer supplied a textured heart model (`heart.glb`) plus loose maps and asked for: a **beautifully
textured heart**, a **normal map** for surface relief, and — the core ask — an **alpha-channel / mask blend**
that transitions the healthy myocardium into an infarcted one (a black necrosis patch) as an infarct
"develops." On Windows this shipped as four features, all to be mirrored on Android:

1. **Textured, normal-mapped heart** — render the GLB with its base-colour texture + tangent-space normal map.
2. **Infarct transition** — per-pixel blend `final = lerp(healthy, infarct, mask * progress)`, gated by a
   grayscale mask and driven by a 0..1 `progress`; exposed as a slider + a ~6 s "develop infarct" animation.
3. **Heart isolation** — the customer GLB is actually a whole ECG teaching scene (human silhouette + ECG
   lead system/axes/text wrapped around a comparatively tiny heart). The heart dialog hides that scaffolding
   and frames the camera on the heart alone.
4. **Leads-scheme toggle** — the "Схема отведений" button shows the scaffolding back and reframes to the
   whole scene.

**Reference (Windows) changes:**
- `src/CardioSimulator.Core/Domain/InfarctTextureBlender.cs` — the pure blend `final = lerp(healthy, infarct, mask*progress)` (+ `tests/CardioSimulator.Core.Tests/InfarctTextureBlenderTests.cs`).
- `src/CardioSimulator.App/Controls/InfarctTextureSet.cs` — decodes the maps, wraps blended output as a GPU texture; sidecar path convention `<model>.healthy|infarct|mask.<ext>`.
- `src/CardioSimulator.App/Controls/Heart3DDialog.cs` — `SetupInfarct` / `ApplyInfarctProgress` / `AdvanceInfarct` / `BuildInfarctControls` (slider + animation); `IsolateHeart` (hide scaffolding + reframe); `ToggleLeadsScheme` (button ↔ "Скрыть схему"); handles Phong **and** PBR materials.
- Assets: `src/CardioSimulator.App/Assets/Models/heart.glb` + `heart.healthy.jpg` + `heart.infarct.jpg` + `heart.mask.jpg`.

### 1.1 CRITICAL platform difference — Android is three.js in a WebView

Windows renders natively (HelixToolkit.SharpDX / Direct3D). **Android renders the heart with three.js (WebGL)
inside a `WebView`.** None of the Windows rendering code ports directly — this is a **re-implementation in
JavaScript/three.js + Kotlin**, not a translation. The relevant Android pieces:

- `app/src/main/java/com/example/cardiosimulator/ui/components/Heart3DViewer.kt` — hosts the `WebView`, builds
  the scene as an **inline HTML string** (`three.module.js` + `GLTFLoader` + `OrbitControls`), and defines the
  `window.setXxx(...)` JS hooks (lines ~256-262). `Heart3DController` calls them via `evaluateJavascript`.
- `app/src/main/assets/heart3d/conduction.js` — `ConductionSystemRenderer`, which **holds the loaded model**
  and already implements `setModel` / `setXray` / `setCutaway` / `setCutPosition`. The natural home for the
  model/material logic (infarct blend, scaffold hiding).
- `app/src/main/java/com/example/cardiosimulator/ui/dialogs/Heart3DDialog.kt` — the Compose control panel
  (play/pause, BPM, X-ray, edit pathway, cutaway). New controls go here.
- `app/src/main/assets/heart3d/heart.glb` — **currently a different 97 MB model** (see Part E).

---

## 2. Part E FIRST — Assets & model alignment (do this before anything else)

**The mask and infarct textures are authored against the customer GLB's UV atlas (1024²).** Android currently
ships a *different* `heart.glb` (~97 MB). If you keep it, `heart.mask.jpg` / `heart.infarct.jpg` will map to
the wrong places and the necrosis will appear as garbage. Two options:

- **(Recommended) Use the customer model for true parity.** Copy the Windows assets into `app/src/main/assets/heart3d/`:
  - `heart.glb`  ← `Win/src/CardioSimulator.App/Assets/Models/heart.glb` (replaces the 97 MB file; also an 8 MB win)
  - `heart.infarct.jpg`, `heart.mask.jpg` ← the Windows sidecars.
  - `heart.healthy.jpg` is **not needed** on Android — the shader uses the material's own base map as the healthy layer (see Part C). Include it only if you choose the CPU-canvas variant.
  - Note: 97 MB in `assets/` bloats the APK and is likely why load is slow; switching to the 8 MB GLB is a real improvement. If both models must coexist, keep the customer one under a distinct name and point `modelPath` at it.
- **(Only if Android must keep its own model)** the mask/infarct maps must be **re-baked to that model's UVs** by
  the artist. Do not reuse the customer maps as-is. The rest of this plan (Parts B/C/D) still applies.

`modelPath` default is in `Heart3DViewer.kt` (`modelPath: String = "heart3d/heart.glb"`). WebView loads assets
via `https://appassets.androidplatform.net/assets/...`, so reference textures the same way.

---

## 3. Part A: Textured heart + normal map

three.js `GLTFLoader` already imports the glTF `baseColorTexture` → `MeshStandardMaterial.map` and
`normalTexture` → `.normalMap`, and lights the relief using screen-space derivatives when the mesh has no
tangents — so **the textured, normal-mapped heart should render with no code change once the correct GLB loads**
(unlike Windows, which needed `RenderNormalMap`/`EnableAutoTangent`). Verify after Part E:

- If relief looks flat, ensure the mesh has UVs and that `material.normalScale` isn't zeroed; three.js computes
  a derivative tangent basis by default. Only if needed, call `geometry.computeTangents()` (requires a
  `tangent` attribute path) — usually unnecessary.
- Confirm `map.colorSpace = THREE.SRGBColorSpace` (GLTFLoader sets this) so colours aren't washed out.

No new Android code for Part A beyond loading the right model.

---

## 4. Part B: Heart isolation + camera framing

The GLB is a full scene; today the loader frames the **whole** model Box3 (`Heart3DViewer.kt` ~lines 209-220),
so the mannequin fills the view and the heart is tiny. Mirror Windows `IsolateHeart`.

In the `GLTFLoader.load(...)` success callback (`Heart3DViewer.kt`), **after** `scene.add(model)` and **before**
the camera-fit block, hide the scaffolding and frame on the heart. Keep references for Part D.

```js
const SCAFFOLD_TOKENS = ['silhouette', 'human', 'ecg', 'lead', 'axes', 'text'];
const heartBox = new THREE.Box3();
let hasScaffold = false;
model.traverse((o) => {
  if (!o.isMesh) return;
  const name = (o.name || '').toLowerCase();
  if (SCAFFOLD_TOKENS.some(t => name.includes(t))) {
    o.visible = false;
    o.userData.scaffold = true;     // remembered for the leads-scheme toggle
    hasScaffold = true;
  } else {
    heartBox.expandByObject(o);     // heart + coronary vessels define the frame
  }
});
window.__fullBox  = new THREE.Box3().setFromObject(model);  // whole scene, for the scheme view
window.__heartBox = hasScaffold ? heartBox : window.__fullBox;
window.__hasScaffold = hasScaffold;
frameCamera(window.__heartBox);   // extract the existing fit math into frameCamera(box)
if (typeof Android !== 'undefined') Android.onScaffoldAvailable(hasScaffold); // enable/disable the button
```

Refactor the existing camera-fit block (center/size/maxDim/`cameraZ`, `camera.position`, `controls.target`)
into a reusable `function frameCamera(box) { ... }` so Part D can reframe. Preserve current behaviour for a
plain model: when `hasScaffold` is false, `__heartBox === __fullBox` and framing is unchanged.

Add an `@JavascriptInterface fun onScaffoldAvailable(has: Boolean)` to `Heart3DBridge` (`Heart3DViewer.kt`) and
surface it so the dialog can enable/disable the leads-scheme control (Part D).

---

## 5. Part C: Infarct mask-blend (progress slider + animation)

**Recommended: a GLSL blend via `material.onBeforeCompile`** — GPU, smooth, keeps the normal map. (A CPU
`CanvasTexture` variant that mirrors the Windows byte-blend exactly is documented at the end as a fallback.)

### C.1 JS — blend shader + hooks (in `conduction.js` `setModel`, or in the load callback)

Apply to each heart-skin material (the ones that carry a base map — the scaffolding has none):

```js
const heartMats = [];
model.traverse((o) => { if (o.isMesh && o.material && o.material.map && !o.userData.scaffold) heartMats.push(o.material); });

const tl = new THREE.TextureLoader();
const A = 'https://appassets.androidplatform.net/assets/heart3d/';
const infarctTex = tl.load(A + 'heart.infarct.jpg');
const maskTex    = tl.load(A + 'heart.mask.jpg');
infarctTex.colorSpace = THREE.SRGBColorSpace;   // colour
maskTex.colorSpace    = THREE.NoColorSpace;     // data — do NOT sRGB-decode the mask
// Match the base map's UV orientation so the blend lines up:
[infarctTex, maskTex].forEach(t => { t.wrapS = t.wrapT = THREE.RepeatWrapping; });

const uProgress = { value: 0.0 };               // shared 0..1 uniform for all heart mats
window.__uProgress = uProgress;

for (const mat of heartMats) {
  if (infarctTex.flipY !== undefined && mat.map) { infarctTex.flipY = mat.map.flipY; maskTex.flipY = mat.map.flipY; }
  mat.onBeforeCompile = (shader) => {
    shader.uniforms.uInfarct  = { value: infarctTex };
    shader.uniforms.uMask     = { value: maskTex };
    shader.uniforms.uProgress = uProgress;
    shader.fragmentShader =
      'uniform sampler2D uInfarct;\nuniform sampler2D uMask;\nuniform float uProgress;\n' + shader.fragmentShader;
    // After the base colour is sampled into diffuseColor, blend toward the infarct where the mask is white.
    // NOTE: three r152+ uses `vMapUv`; older builds use `vUv` — check vendor/three.module.js version.
    shader.fragmentShader = shader.fragmentShader.replace(
      '#include <map_fragment>',
      `#include <map_fragment>
       {
         float _w = texture2D(uMask, vMapUv).r * uProgress;
         vec3 _inf = texture2D(uInfarct, vMapUv).rgb;
         diffuseColor.rgb = mix(diffuseColor.rgb, _inf, _w);
       }`
    );
  };
  mat.needsUpdate = true;
}

window.setInfarctProgress = (p) => { window.__uProgress.value = Math.max(0, Math.min(1, p)); };

// ~6 s "develop infarct" animation, ticked from the existing animate() loop.
let infarctPlaying = false, infarctStart = 0, infarctFrom = 0;
window.playInfarct = () => {
  infarctFrom = (window.__uProgress.value >= 0.999) ? 0 : window.__uProgress.value;
  if (infarctFrom === 0) window.__uProgress.value = 0;
  infarctStart = performance.now(); infarctPlaying = true;
};
window.stopInfarct = () => { infarctPlaying = false; };
// inside animate(): if (infarctPlaying) { const p = Math.min(1, infarctFrom + (performance.now()-infarctStart)/6000);
//                                          window.__uProgress.value = p; if (p >= 1) infarctPlaying = false; }
```

Verify the injection anchor exists (`grep '#include <map_fragment>'` after logging `shader.fragmentShader`);
three.js MeshStandardMaterial includes it. If the material is unlit/`MeshBasicMaterial`, the same anchor works.

### C.2 Kotlin — controller hooks (`Heart3DViewer.kt`, `Heart3DController`)

```kotlin
fun setInfarctProgress(progress: Float) {
    webView?.evaluateJavascript("window.setInfarctProgress($progress)", null)
}
fun playInfarct() { webView?.evaluateJavascript("window.playInfarct()", null) }
fun stopInfarct() { webView?.evaluateJavascript("window.stopInfarct()", null) }
```

### C.3 Compose — controls (`Heart3DDialog.kt`)

Add a section mirroring the existing X-ray/cutaway blocks (reference Windows `BuildInfarctControls`):

```kotlin
Text(stringResource(R.string.monitor_3d_infarct))           // "Инфаркт (некроз)"
var infarct by remember { mutableStateOf(0f) }
Text(infarctLabel(infarct))                                 // Здоровый миокард ↔ Инфаркт: N% ↔ Полный инфаркт
Slider(value = infarct, onValueChange = { infarct = it; controller.setInfarctProgress(it) },
       valueRange = 0f..1f, colors = SliderDefaults.colors(thumbColor = WindowsBlue, activeTrackColor = WindowsBlue))
Button(onClick = { controller.playInfarct() }, colors = ButtonDefaults.buttonColors(containerColor = WindowsBlue)) {
    Text(stringResource(R.string.monitor_3d_develop_infarct))   // "▶ Развитие инфаркта"
}
```

If you want the slider thumb to track the running animation, poll `window.__uProgress.value` via
`evaluateJavascript` with a callback on a ~50 ms tick while playing, or push progress from JS back through a new
`@JavascriptInterface` callback. This is optional polish — Windows drives the slider from its render loop; on
Android the simplest parity is "button plays the animation; slider is for manual scrub."

Strings (`values/strings.xml` + `values-ru/strings.xml`):

| key | en | ru |
|---|---|---|
| `monitor_3d_infarct` | Infarct (necrosis) | Инфаркт (некроз) |
| `monitor_3d_develop_infarct` | ▶ Develop infarct | ▶ Развитие инфаркта |
| `monitor_3d_infarct_healthy` | Healthy myocardium | Здоровый миокард |
| `monitor_3d_infarct_full` | Full infarct | Полный инфаркт |
| `monitor_3d_infarct_percent` | `Infarct: %1$d%%` | `Инфаркт: %1$d%%` |

---

## 6. Part D: Leads-scheme toggle ("Схема отведений")

Android has no such button yet — add one. Mirror Windows `ToggleLeadsScheme`.

### D.1 JS (`Heart3DViewer.kt` inline script, uses Part B's `__fullBox`/`__heartBox`/`scaffold` flags)

```js
let leadsSchemeOn = false;
window.setLeadsScheme = (on) => {
  leadsSchemeOn = on;
  model.traverse((o) => { if (o.userData.scaffold) o.visible = on; });
  frameCamera(on ? window.__fullBox : window.__heartBox);
  if (window.__reapplyXray) window.__reapplyXray();   // if X-ray is on, re-apply to newly shown meshes
};
```

### D.2 Kotlin controller

```kotlin
fun setLeadsScheme(on: Boolean) { webView?.evaluateJavascript("window.setLeadsScheme($on)", null) }
```

### D.3 Compose control (`Heart3DDialog.kt`)

Add a button/toggle that flips label between "Схема отведений" and "Скрыть схему" and is **disabled** until
`onScaffoldAvailable(true)` (Part B). Strings:

| key | en | ru |
|---|---|---|
| `monitor_3d_lead_scheme` | Leads scheme | Схема отведений |
| `monitor_3d_lead_scheme_hide` | Hide scheme | Скрыть схему |

(Reuse an existing `monitor_3d_lead_scheme` key if the Android string table already has one for the label.)

---

## 7. What differs from Windows / what NOT to port

- **No native-material or `TextureModel`/`PBRMaterialCore`/`PhongMaterialCore` code.** That is HelixToolkit-only.
  The Android equivalent is three.js `MeshStandardMaterial` + `onBeforeCompile`.
- **`InfarctTextureBlender.cs` is not ported as code** — the blend lives in GLSL (`mix(base, infarct, mask*progress)`).
  Its *formula* and the mask/progress semantics are the contract to preserve. (If you take the CPU-canvas route,
  the byte math is the same as the C# version.)
- **The Windows Phong-vs-PBR branching** (`GetDiffuseOrAlbedo`/`SetDiffuseOrAlbedo`) has no Android analogue —
  three.js gives one material type here.
- **Sidecar-file path convention** (`<model>.healthy|infarct|mask`) is a Windows filesystem detail; on Android
  the maps are fixed asset names under `assets/heart3d/`.

### CPU-canvas fallback (exact Windows parity, if the shader route is undesirable)
Blend on a 2D `<canvas>`: draw `healthy`, read pixels, draw `infarct`+`mask`, compute
`out = healthy*(1-mask*p) + infarct*(mask*p)` per pixel (identical to `InfarctTextureBlender.BlendBgra`), set the
result as a `THREE.CanvasTexture` on `material.map`, `map.needsUpdate = true`. Throttle rebuilds during the
animation (mirror the Windows coalescing). Keep `material.normalMap` untouched so relief still lights. This needs
`heart.healthy.jpg` as an explicit asset (the healthy layer), unlike the shader route.

---

## 8. Verification

### 8.1 Manual (emulator/device)
1. After Part E, open the 3D heart dialog → the heart renders **textured** with visible **relief** (normal map),
   framed on the heart (not the mannequin).
2. Drag the **infarct** slider 0→1 → a black necrosis patch fades in only where the mask is white; rest of the
   myocardium unchanged. "▶ Развитие инфаркта" animates it over ~6 s; label reads Здоровый миокард → Инфаркт: N%
   → Полный инфаркт.
3. **Схема отведений** → the human silhouette + ECG scaffolding appear and the camera reframes to the whole
   body; button reads "Скрыть схему"; tap again → back to the isolated heart. Button disabled for a plain model.
4. X-ray, cutaway, conduction still work with the new model (regression check).
5. Rotate (OrbitControls) to confirm the necrosis sits at the anatomically-correct spot(s) per the mask.

### 8.2 Notes from the Windows verification (avoid the same traps)
- The necrosis spots are **small and localized** (a few mask blobs), not one big patch — they may face away from
  the default camera. Rotate to confirm.
- On Windows the heart was invisible until isolation was added *and* the camera reframed on the heart box —
  don't skip the reframe in Part B.
- Watch the three.js version: the `map_fragment` UV varying is `vMapUv` (r152+) vs `vUv` (older). Check
  `assets/heart3d/vendor/three.module.js`.

---

## 9. Commit

```
feat(3d): textured heart + mask-blend infarct + heart isolation + leads-scheme toggle

Android parity for the Windows 3D-heart work: render the customer GLB with its
base-colour texture and normal-map relief, blend healthy→infarct via a mask and
0..1 progress (slider + ~6s animation) in a three.js onBeforeCompile shader,
hide the bundled silhouette/ECG scaffolding and frame on the heart, and add a
"Схема отведений" toggle to show the scaffolding and reframe to the whole scene.
```
