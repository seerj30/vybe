"""Compares a fresh pipeline build with the committed files (CI job "assets").

    python3 assets/tools/compare_build.py <committed copy> <repository>

Both folders hold assets/published/3d and assets/manifests. Models, the base avatar and the manifests must be
the same bytes. Thumbnails are raster renders: Cycles takes other CPU code paths on an Apple-silicon Mac (Rosetta)
than on a native x86 runner, so a few edge pixels can differ. A thumbnail passes when at most 2 % of its pixels
differ by more than 24 levels in any channel; the manifests are compared without the thumbnail URLs.
"""

from __future__ import annotations

import json
import os
import re
import struct
import sys
import zlib
from typing import Dict, List, Tuple

PIXEL_DELTA = 24
MAX_CHANGED = 0.02
HASHED = re.compile(r"^(?P<stem>[a-z0-9_]+)\.[0-9a-f]{12}\.(?P<ext>glb|png)$")


def decode_png(data: bytes) -> Tuple[int, int, int, bytes]:
    """8-bit RGB or RGBA PNG → (width, height, channels, raw pixels)."""
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a PNG file")
    i, idat, width, height, channels = 8, [], 0, 0, 0
    while i < len(data):
        (length,) = struct.unpack(">I", data[i:i + 4])
        kind, body = data[i + 4:i + 8], data[i + 8:i + 8 + length]
        if kind == b"IHDR":
            width, height, depth, color, _c, _f, interlace = struct.unpack(">IIBBBBB", body)
            if depth != 8 or color not in (2, 6) or interlace != 0:
                raise ValueError(f"unsupported PNG (depth {depth}, color type {color}, interlace {interlace})")
            channels = 4 if color == 6 else 3
        elif kind == b"IDAT":
            idat.append(body)
        i += 12 + length
    raw = zlib.decompress(b"".join(idat))
    stride = width * channels
    out = bytearray(height * stride)
    prev = bytearray(stride)
    for y in range(height):
        f = raw[y * (stride + 1)]
        line = bytearray(raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)])
        for x in range(stride):
            a = line[x - channels] if x >= channels else 0
            b = prev[x]
            c = prev[x - channels] if x >= channels else 0
            if f == 1:
                line[x] = (line[x] + a) & 0xFF
            elif f == 2:
                line[x] = (line[x] + b) & 0xFF
            elif f == 3:
                line[x] = (line[x] + (a + b) // 2) & 0xFF
            elif f == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 0xFF
        out[y * stride:(y + 1) * stride] = line
        prev = line
    return width, height, channels, bytes(out)


def changed_share(a: bytes, b: bytes) -> float:
    wa, ha, ca, pa = decode_png(a)
    wb, hb, cb, pb = decode_png(b)
    if (wa, ha, ca) != (wb, hb, cb):
        return 1.0
    changed = 0
    for p in range(0, len(pa), ca):
        if max(abs(pa[p + k] - pb[p + k]) for k in range(ca)) > PIXEL_DELTA:
            changed += 1
    return changed / (wa * ha)


def files(root: str) -> Dict[str, Tuple[str, str]]:
    """'<folder>/<stem>.<ext>' → (file name, full path) under assets/published/3d."""
    base = os.path.join(root, "assets", "published", "3d")
    out = {}
    for folder in sorted(os.listdir(base)):
        for name in sorted(os.listdir(os.path.join(base, folder))):
            m = HASHED.match(name)
            key = f"{folder}/{m.group('stem')}.{m.group('ext')}" if m else f"{folder}/{name}"
            out[key] = (name, os.path.join(base, folder, name))
    return out


def manifest(root: str, name: str) -> dict:
    with open(os.path.join(root, "assets", "manifests", name), encoding="utf-8") as f:
        doc = json.load(f)
    if name.startswith(("catalog", "room")):
        for item in doc["items"]:
            item.pop("thumbnailUrl", None)
    return doc


def main(committed: str, rebuilt: str) -> int:
    errors: List[str] = []
    old, new = files(committed), files(rebuilt)
    for key in sorted(set(old) | set(new)):
        if key not in old or key not in new:
            errors.append(f"{key}: only in the {'rebuild' if key in new else 'commit'}")
            continue
        with open(old[key][1], "rb") as f:
            a = f.read()
        with open(new[key][1], "rb") as f:
            b = f.read()
        if key.endswith(".png"):
            share = changed_share(a, b)
            if share > MAX_CHANGED:
                errors.append(f"{key}: {share:.1%} of the pixels changed (limit {MAX_CHANGED:.0%})")
            elif share > 0:
                sys.stdout.write(f"note: {key}: {share:.2%} of the pixels differ (renderer noise, accepted)\n")
        elif a != b:
            errors.append(f"{key}: the rebuild has other bytes ({old[key][0]} → {new[key][0]})")
    for name in ("avatar-3d.v1.json", "catalog.v1.json", "room.v1.json"):
        if manifest(committed, name) != manifest(rebuilt, name):
            errors.append(f"{name}: the rebuild differs (thumbnail URLs not compared)")
    for e in errors:
        sys.stderr.write(f"error: {e}\n")
    if errors:
        sys.stderr.write("assets/source/build.sh output differs from the committed files. Run it and commit the result.\n")
        return 1
    sys.stdout.write(f"rebuild matches the commit: {len(new)} files\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2]))
