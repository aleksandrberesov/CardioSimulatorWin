"""
Derives the three infarct sidecar maps for the bundled 3D heart's UV atlas.

    heart.healthy.jpg  the model's own base-colour atlas (what renders at progress 0)
    heart.mask.png     WHERE necrosis can appear
    heart.infarct.jpg  what that tissue looks like fully necrotic

The app blends these as `lerp(healthy, infarct, mask * progress)` (see
CardioSimulator.Core/Domain/InfarctTextureBlender.cs) and also samples the mask to decide which
vertices are non-conducting scar for the depolarisation wavefront.

WHY THIS EXISTS. The maps are keyed to the model's UV layout, so they cannot survive a model swap:
maps painted for one heart land on random places on the next. Rather than hand-paint a new blob,
this derives the mask from the MESH — an anterior-apical (LAD / anteroseptal) territory projected
through the UVs — so it lands on the right anatomy whatever the artist did with the atlas.

This is a STAND-IN until the customer ships the territory atlas of asset-spec-3d-heart.md §6
(heart.territories.png + territories.json + coronaries.json), which replaces a single fixed
territory with a per-artery choice. See §17 of that spec.

Needs Python 3 with numpy + Pillow (not part of the app build — this is a one-shot asset step):

    py -m pip install numpy pillow
    py tools/make-heart-infarct-maps.py

It reads src/CardioSimulator.App/Assets/Models/heart.glb and writes the three maps beside it.
"""

import io
import json
import struct
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter

REPO = Path(__file__).resolve().parent.parent
MODELS = REPO / "src" / "CardioSimulator.App" / "Assets" / "Models"
GLB = MODELS / "heart.glb"

HEART_MESH = "hear"          # the realistic outer skin; the cutaway skin has its own atlas
SIZE = 2048                  # must match the model's base-colour atlas

COMPONENT = {5120: "b", 5121: "B", 5122: "h", 5123: "H", 5125: "I", 5126: "f"}
COMPONENT_SIZE = {5120: 1, 5121: 1, 5122: 2, 5123: 2, 5125: 4, 5126: 4}
NUM = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


def read_glb(path):
    """Returns (gltf json, BIN chunk bytes)."""
    with open(path, "rb") as f:
        _magic, _ver, length = struct.unpack("<III", f.read(12))
        chunks = []
        while f.tell() < length:
            clen, ctype = struct.unpack("<II", f.read(8))
            chunks.append((ctype, f.read(clen)))
    gltf = json.loads(next(d for t, d in chunks if t == 0x4E4F534A).decode("utf-8"))
    return gltf, next(d for t, d in chunks if t == 0x004E4942)


def accessor(gltf, blob, index):
    acc = gltf["accessors"][index]
    view = gltf["bufferViews"][acc["bufferView"]]
    fmt = COMPONENT[acc["componentType"]]
    size = COMPONENT_SIZE[acc["componentType"]]
    n = NUM[acc["type"]]
    stride = view.get("byteStride") or size * n
    base = view.get("byteOffset", 0) + acc.get("byteOffset", 0)
    raw = np.frombuffer(blob, dtype=np.uint8)
    out = np.zeros((acc["count"], n), dtype=np.dtype(fmt))
    for k in range(acc["count"]):
        start = base + k * stride
        out[k] = np.frombuffer(raw[start:start + size * n].tobytes(), dtype=np.dtype(fmt), count=n)
    return out.reshape(-1) if n == 1 else out


def mesh_world(gltf, blob, name):
    """World-space positions, triangle indices and UVs of the named mesh."""
    nodes = gltf["nodes"]
    children = {c for n in nodes for c in n.get("children", [])}
    ident = np.eye(4)
    found = []

    def walk(i, parent):
        node = nodes[i]
        local = np.array(node.get("matrix", np.eye(4).T.reshape(-1)), dtype=np.float64).reshape(4, 4).T
        world = parent @ local
        if node.get("mesh") is not None and gltf["meshes"][node["mesh"]].get("name") == name:
            for prim in gltf["meshes"][node["mesh"]]["primitives"]:
                pos = accessor(gltf, blob, prim["attributes"]["POSITION"]).astype(np.float64)
                found.append((
                    (world[:3, :3] @ pos.T).T + world[:3, 3],
                    accessor(gltf, blob, prim["indices"]).astype(np.int64).reshape(-1, 3),
                    accessor(gltf, blob, prim["attributes"]["TEXCOORD_0"]).astype(np.float64),
                ))
        for c in node.get("children", []):
            walk(c, world)

    for i in range(len(nodes)):
        if i not in children:
            walk(i, ident)
    if not found:
        sys.exit(f"mesh {name!r} not found in {GLB}")
    if len(found) > 1:
        sys.exit(f"mesh {name!r} has {len(found)} primitives; this script assumes one")
    return found[0]


def base_colour_atlas(gltf, blob, mesh_name):
    """The mesh's embedded base-colour texture, as an RGB array."""
    for node in gltf["nodes"]:
        m = node.get("mesh")
        if m is None or gltf["meshes"][m].get("name") != mesh_name:
            continue
        prim = gltf["meshes"][m]["primitives"][0]
        material = gltf["materials"][prim["material"]]
        tex = material["pbrMetallicRoughness"]["baseColorTexture"]["index"]
        image = gltf["images"][gltf["textures"][tex]["source"]]
        view = gltf["bufferViews"][image["bufferView"]]
        off = view.get("byteOffset", 0)
        png = blob[off:off + view["byteLength"]]
        return np.asarray(Image.open(io.BytesIO(png)).convert("RGB")).astype(np.float32)
    sys.exit(f"no base-colour texture on {mesh_name!r}")


def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0.0, 1.0)
    return t * t * (3 - 2 * t)


def territory_weight(V, T):
    """Per-vertex 0..1 weight for the anterior-apical (LAD) territory."""
    # Ventricular gate: the AV plane sits at about y = 0.017 (the model's AV-node anchor), so fade
    # the territory out above it and leave the atria and great vessels healthy.
    gate = smoothstep(0.020, -0.005, V[:, 1])

    # Per-vertex normals, so the territory stays on the wall it starts on instead of wrapping round
    # to the posterior surface (the heart is only ~90 mm deep).
    face = np.cross(V[T[:, 1]] - V[T[:, 0]], V[T[:, 2]] - V[T[:, 0]])
    vn = np.zeros_like(V)
    for k in range(3):
        np.add.at(vn, T[:, k], face)
    vn /= np.maximum(np.linalg.norm(vn, axis=1, keepdims=True), 1e-12)
    facing = smoothstep(-0.15, 0.45, vn @ np.array([0.30, -0.25, 0.92]))

    # Seed on the anterior wall just above the apex. The apex is always LV, so a patch centred there
    # is the anterior LV wall whichever way round the model happens to be built.
    vent = V[V[:, 1] < 0.005]
    apex = vent[vent[:, 1].argmin()]
    front = vent[(vent[:, 2] - 0.8 * vent[:, 1]).argmax()]
    seed = 0.45 * apex + 0.55 * front
    print(f"  apex={np.round(apex, 4)} front={np.round(front, 4)} seed={np.round(seed, 4)}")

    d = np.linalg.norm(V - seed, axis=1)
    return smoothstep(0.055, 0.020, d) * gate * facing  # solid inside 20 mm, feathered out by 55 mm


def rasterise(weight, uv, T):
    """Paints the per-vertex weight through the UV atlas."""
    mask = np.zeros((SIZE, SIZE), np.float32)
    U = np.clip(uv, 0, 1) * (SIZE - 1)   # glTF v = 0 is the top row, same as the app's decoder
    painted = 0
    for tri in T:
        p, w = U[tri], weight[tri]
        if w.max() < 0.004:
            continue
        x0, x1 = max(int(np.floor(p[:, 0].min())), 0), min(int(np.ceil(p[:, 0].max())), SIZE - 1)
        y0, y1 = max(int(np.floor(p[:, 1].min())), 0), min(int(np.ceil(p[:, 1].max())), SIZE - 1)
        if x1 < x0 or y1 < y0:
            continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1), np.arange(y0, y1 + 1))
        det = (p[1, 1] - p[2, 1]) * (p[0, 0] - p[2, 0]) + (p[2, 0] - p[1, 0]) * (p[0, 1] - p[2, 1])
        if abs(det) < 1e-9:
            continue
        l0 = ((p[1, 1] - p[2, 1]) * (gx - p[2, 0]) + (p[2, 0] - p[1, 0]) * (gy - p[2, 1])) / det
        l1 = ((p[2, 1] - p[0, 1]) * (gx - p[2, 0]) + (p[0, 0] - p[2, 0]) * (gy - p[2, 1])) / det
        l2 = 1 - l0 - l1
        eps = -0.004                      # bleed a hair past the edge so UV islands close up
        inside = (l0 >= eps) & (l1 >= eps) & (l2 >= eps)
        if not inside.any():
            continue
        val = (l0 * w[0] + l1 * w[1] + l2 * w[2]).astype(np.float32)
        sub = mask[y0:y1 + 1, x0:x1 + 1]
        np.maximum(sub, np.where(inside, np.clip(val, 0, 1), 0), out=sub)
        painted += 1
    print(f"  rasterised {painted} triangles")

    img = Image.fromarray((np.clip(mask, 0, 1) * 255).astype(np.uint8), "L")
    img = img.filter(ImageFilter.MaxFilter(5))       # dilate over UV seams so islands meet up
    img = img.filter(ImageFilter.GaussianBlur(3))    # soft necrosis border
    return img


def necrotic(healthy):
    """Dark, desaturated, dusky myocardium with the patchy look of a transmural infarct."""
    lum = healthy @ np.array([0.299, 0.587, 0.114], np.float32)
    out = 0.30 * healthy + 0.70 * np.repeat(lum[:, :, None], 3, axis=2)
    out *= np.array([0.42, 0.34, 0.40], np.float32)

    rng = np.random.default_rng(20260921)            # fixed seed: reruns reproduce the same map
    noise = (rng.random((SIZE // 16, SIZE // 16)) * 255).astype(np.uint8)
    noise = np.asarray(Image.fromarray(noise, "L")
                       .resize((SIZE, SIZE), Image.BICUBIC)
                       .filter(ImageFilter.GaussianBlur(6))).astype(np.float32) / 255.0
    return np.clip(out * (0.80 + 0.40 * noise)[:, :, None], 0, 255)


def main():
    gltf, blob = read_glb(GLB)
    V, T, uv = mesh_world(gltf, blob, HEART_MESH)
    print(f"{HEART_MESH}: {len(V)} verts, {len(T)} tris")

    weight = territory_weight(V, T)
    print(f"  weight >0.5 on {100 * (weight > 0.5).mean():.1f}% of verts")

    mask = rasterise(weight, uv, T)
    coverage = np.asarray(mask).astype(np.float32) / 255.0
    print(f"  atlas coverage >0.5: {100 * (coverage > 0.5).mean():.2f}%")

    healthy = base_colour_atlas(gltf, blob, HEART_MESH)
    if healthy.shape[:2] != (SIZE, SIZE):
        sys.exit(f"base-colour atlas is {healthy.shape[:2]}, expected {(SIZE, SIZE)}")

    Image.fromarray(healthy.astype(np.uint8)).save(
        MODELS / "heart.healthy.jpg", quality=95, subsampling=0)
    Image.fromarray(necrotic(healthy).astype(np.uint8)).save(
        MODELS / "heart.infarct.jpg", quality=95, subsampling=0)
    mask.save(MODELS / "heart.mask.png")
    print(f"wrote heart.healthy.jpg / heart.infarct.jpg / heart.mask.png to {MODELS}")


if __name__ == "__main__":
    main()
