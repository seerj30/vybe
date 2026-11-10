"""Minimal glTF 2.0 binary (.glb) reader, Python standard library only.

Used by publish.py (to measure files for the manifests) and check_assets.py (to verify them in CI).
"""

from __future__ import annotations

import json
import struct
from dataclasses import dataclass
from typing import Any, Dict, List, Optional, Sequence, Tuple

GLB_MAGIC = 0x46546C67  # "glTF"
CHUNK_JSON = 0x4E4F534A
CHUNK_BIN = 0x004E4942

_COMPONENT = {5120: ("b", 1), 5121: ("B", 1), 5122: ("h", 2), 5123: ("H", 2), 5125: ("I", 4), 5126: ("f", 4)}
_COUNT = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


class GlbError(ValueError):
    pass


@dataclass
class Glb:
    json: Dict[str, Any]
    bin: bytes

    # ------------------------------------------------------------ basic reads

    def accessor(self, index: int) -> List[Tuple[float, ...]]:
        acc = self.json["accessors"][index]
        fmt, size = _COMPONENT[acc["componentType"]]
        n = _COUNT[acc["type"]]
        view = self.json["bufferViews"][acc["bufferView"]]
        start = view.get("byteOffset", 0) + acc.get("byteOffset", 0)
        stride = view.get("byteStride", size * n)
        out: List[Tuple[float, ...]] = []
        for i in range(acc["count"]):
            off = start + i * stride
            out.append(struct.unpack_from("<" + fmt * n, self.bin, off))
        if acc.get("normalized"):
            scale = float((1 << (8 * size - (1 if fmt.islower() else 0))) - 1)
            out = [tuple(v / scale for v in t) for t in out]
        return out

    def node_names(self) -> List[str]:
        return [n.get("name", "") for n in self.json.get("nodes", [])]

    def mesh_nodes(self) -> List[Dict[str, Any]]:
        return [n for n in self.json.get("nodes", []) if "mesh" in n]

    def primitives(self, node: Dict[str, Any]) -> List[Dict[str, Any]]:
        return self.json["meshes"][node["mesh"]]["primitives"]

    # ------------------------------------------------------------ measures

    def triangles(self, nodes: Optional[Sequence[Dict[str, Any]]] = None) -> int:
        total = 0
        for node in nodes if nodes is not None else self.mesh_nodes():
            for prim in self.primitives(node):
                mode = prim.get("mode", 4)
                if mode != 4:
                    raise GlbError(f"primitive mode {mode} is not TRIANGLES")
                if "indices" in prim:
                    count = self.json["accessors"][prim["indices"]]["count"]
                else:
                    count = self.json["accessors"][prim["attributes"]["POSITION"]]["count"]
                total += count // 3
        return total

    def draw_calls(self, nodes: Optional[Sequence[Dict[str, Any]]] = None) -> int:
        return sum(len(self.primitives(n)) for n in (nodes if nodes is not None else self.mesh_nodes()))

    def images(self) -> List[Tuple[str, bytes]]:
        out = []
        for img in self.json.get("images", []):
            if "bufferView" not in img:
                raise GlbError("external image URIs are not allowed")
            view = self.json["bufferViews"][img["bufferView"]]
            off = view.get("byteOffset", 0)
            out.append((img.get("mimeType", ""), self.bin[off:off + view["byteLength"]]))
        return out

    def texture_bytes(self) -> int:
        return sum(len(data) for _mime, data in self.images())

    def skin_joint_names(self) -> List[str]:
        names = self.node_names()
        out: List[str] = []
        for skin in self.json.get("skins", []):
            for j in skin["joints"]:
                if names[j] not in out:
                    out.append(names[j])
        return out

    def bone_rest(self) -> Dict[str, Tuple[Tuple[float, ...], Tuple[float, ...], Optional[str]]]:
        """Rest transform of every joint: name -> (translation, rotation, parent name)."""
        nodes = self.json.get("nodes", [])
        parent: Dict[int, int] = {}
        for i, n in enumerate(nodes):
            for c in n.get("children", []):
                parent[c] = i
        joints = {j for skin in self.json.get("skins", []) for j in skin["joints"]}
        out = {}
        for j in sorted(joints):
            n = nodes[j]
            p = parent.get(j)
            out[n["name"]] = (
                tuple(n.get("translation", (0, 0, 0))),
                tuple(n.get("rotation", (0, 0, 0, 1))),
                nodes[p]["name"] if p is not None and p in joints else None,
            )
        return out

    def morph_targets(self, node: Dict[str, Any]) -> List[str]:
        mesh = self.json["meshes"][node["mesh"]]
        return list(mesh.get("extras", {}).get("targetNames", []))

    def positions(self, node: Dict[str, Any]) -> List[Tuple[Tuple[float, ...], Tuple[float, ...], Tuple[float, ...]]]:
        """(position, joints, weights) of every vertex of a skinned mesh node."""
        out = []
        for prim in self.primitives(node):
            a = prim["attributes"]
            pos = self.accessor(a["POSITION"])
            joints = self.accessor(a["JOINTS_0"]) if "JOINTS_0" in a else [(0, 0, 0, 0)] * len(pos)
            weights = self.accessor(a["WEIGHTS_0"]) if "WEIGHTS_0" in a else [(1, 0, 0, 0)] * len(pos)
            out.extend(zip(pos, joints, weights))
        return out


def read(data: bytes) -> Glb:
    if len(data) < 20:
        raise GlbError("file too short")
    magic, version, length = struct.unpack_from("<III", data, 0)
    if magic != GLB_MAGIC:
        raise GlbError("not a .glb file")
    if version != 2:
        raise GlbError(f"glTF container version {version}, need 2")
    if length != len(data):
        raise GlbError("length field does not match the file size")
    json_len, json_type = struct.unpack_from("<II", data, 12)
    if json_type != CHUNK_JSON:
        raise GlbError("first chunk is not JSON")
    doc = json.loads(data[20:20 + json_len].decode("utf-8"))
    binary = b""
    off = 20 + json_len
    if off < len(data):
        bin_len, bin_type = struct.unpack_from("<II", data, off)
        if bin_type != CHUNK_BIN:
            raise GlbError("second chunk is not BIN")
        binary = data[off + 8:off + 8 + bin_len]
    if doc.get("asset", {}).get("version") != "2.0":
        raise GlbError("asset.version is not 2.0")
    if doc.get("extensionsRequired"):
        raise GlbError(f"required extensions are not supported: {doc['extensionsRequired']}")
    return Glb(doc, binary)


def image_size(mime: str, data: bytes) -> Tuple[int, int]:
    """Width and height of a PNG or JPEG image."""
    if data[:8] == b"\x89PNG\r\n\x1a\n":
        w, h = struct.unpack(">II", data[16:24])
        return w, h
    if data[:2] == b"\xff\xd8":
        i = 2
        while i < len(data):
            if data[i] != 0xFF:
                i += 1
                continue
            marker = data[i + 1]
            seg = struct.unpack(">H", data[i + 2:i + 4])[0]
            if marker in (0xC0, 0xC1, 0xC2):
                h, w = struct.unpack(">HH", data[i + 5:i + 9])
                return w, h
            i += 2 + seg
    raise GlbError(f"cannot read the size of a {mime or 'unknown'} image")
