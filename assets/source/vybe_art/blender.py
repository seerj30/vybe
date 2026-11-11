"""Blender side of the pipeline: objects, armature, actions, glTF export and thumbnail renders.

Only this module imports bpy. It runs inside Blender 4.5 LTS (the `bpy` module in assets/source/Dockerfile,
or `blender -b -P assets/source/build.py` in the Blender app).
"""

from __future__ import annotations

import math
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

import bpy
from mathutils import Matrix, Quaternion, Vector

from .geom import Mesh
from .palette import Material
from .rig import BONES, MORPH_BODY_B

FPS = 30


def reset() -> None:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    _materials.clear()
    scene = bpy.context.scene
    scene.render.fps = FPS
    scene.frame_start = 0
    scene.frame_end = 0


def armature() -> bpy.types.Object:
    data = bpy.data.armatures.new("vybe_rig")
    obj = bpy.data.objects.new("vybe_rig", data)
    bpy.context.scene.collection.objects.link(obj)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode="EDIT")
    for b in BONES:
        eb = data.edit_bones.new(b.name)
        eb.head = b.head
        eb.tail = b.tail
        eb.roll = 0.0
        eb.use_deform = True
        if b.parent:
            eb.parent = data.edit_bones[b.parent]
    bpy.ops.object.mode_set(mode="OBJECT")
    for pb in obj.pose.bones:
        pb.rotation_mode = "QUATERNION"
    return obj


_materials: Dict[tuple, bpy.types.Material] = {}


def srgb_to_linear(c: float) -> float:
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def hex_linear(hex_color: str) -> Tuple[float, float, float]:
    h = hex_color.lstrip("#")
    return tuple(round(srgb_to_linear(int(h[i:i + 2], 16) / 255), 6) for i in (0, 2, 4))  # type: ignore[return-value]


def material(m: Material) -> bpy.types.Material:
    """One Blender material per distinct look. The first one keeps the role name (exported files hold one
    asset, so their names are exact); a second look with the same name in a preview scene gets a suffix."""
    key = (m.name, m.color, m.emissive, m.double_sided)
    mat = _materials.get(key)
    if mat is not None:
        return mat
    mat = bpy.data.materials.new(m.name)
    _materials[key] = mat
    mat.use_nodes = True
    mat.use_backface_culling = not m.double_sided
    bsdf = mat.node_tree.nodes["Principled BSDF"]
    r, g, b = hex_linear(m.color)
    bsdf.inputs["Base Color"].default_value = (r, g, b, 1.0)
    bsdf.inputs["Metallic"].default_value = 0.0
    bsdf.inputs["Roughness"].default_value = 1.0
    if m.emissive:
        er, eg, eb = hex_linear(m.emissive)
        bsdf.inputs["Emission Color"].default_value = (er, eg, eb, 1.0)
        bsdf.inputs["Emission Strength"].default_value = 1.0
    return mat


def mesh_object(
    rig: bpy.types.Object,
    mesh: Mesh,
    materials: Dict[str, Material],
    morph: Optional[Mesh] = None,
    name: Optional[str] = None,
) -> bpy.types.Object:
    """A skinned object from a geom.Mesh; morph (same topology) becomes the body_b shape key."""
    me = bpy.data.meshes.new(name or mesh.name)
    me.from_pydata(mesh.verts, [], mesh.faces)
    names = mesh.materials()
    for n in names:
        me.materials.append(material(materials[n]))
    index = {n: i for i, n in enumerate(names)}
    for poly, mat_name in zip(me.polygons, mesh.mats):
        poly.material_index = index[mat_name]
        poly.use_smooth = False
    obj = bpy.data.objects.new(name or mesh.name, me)
    bpy.context.scene.collection.objects.link(obj)
    obj.parent = rig
    groups: Dict[str, bpy.types.VertexGroup] = {}
    for b in BONES:
        if any(b.name in w for w in mesh.weights):
            groups[b.name] = obj.vertex_groups.new(name=b.name)
    for i, w in enumerate(mesh.weights):
        for bone, value in sorted(w.items()):
            groups[bone].add([i], value, "REPLACE")
    mod = obj.modifiers.new("rig", "ARMATURE")
    mod.object = rig
    if morph is not None:
        if not mesh.same_topology(morph):
            raise ValueError(f"{mesh.name}: body_b shape has another topology")
        obj.shape_key_add(name="Basis", from_mix=False)
        key = obj.shape_key_add(name=MORPH_BODY_B, from_mix=False)
        for i, p in enumerate(morph.verts):
            key.data[i].co = p
    return obj


def static_object(mesh: Mesh, materials: Dict[str, Material], name: Optional[str] = None) -> bpy.types.Object:
    """A plain (not skinned) object from a geom.Mesh: room items have no rig."""
    me = bpy.data.meshes.new(name or mesh.name)
    me.from_pydata(mesh.verts, [], mesh.faces)
    names = mesh.materials()
    for n in names:
        me.materials.append(material(materials[n]))
    index = {n: i for i, n in enumerate(names)}
    for poly, mat_name in zip(me.polygons, mesh.mats):
        poly.material_index = index[mat_name]
        poly.use_smooth = False
    obj = bpy.data.objects.new(name or mesh.name, me)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def place(obj: bpy.types.Object, location: Sequence[float], z_rotation: float) -> None:
    obj.location = location
    obj.rotation_euler = (0.0, 0.0, z_rotation)


# ---------------------------------------------------------------- animation


def rest_rotation(rig: bpy.types.Object, bone: str) -> Quaternion:
    return rig.data.bones[bone].matrix_local.to_quaternion()


def pose_quaternion(rig: bpy.types.Object, bone: str, euler_deg: Sequence[float]) -> Quaternion:
    """Rotation given in avatar space (X right, Y back, Z up; degrees, XYZ order) as a bone-local rotation."""
    from mathutils import Euler

    world = Euler([math.radians(a) for a in euler_deg], "XYZ").to_quaternion()
    rest = rest_rotation(rig, bone)
    return rest.inverted() @ world @ rest


def action(
    rig: bpy.types.Object,
    name: str,
    keys: Sequence[Tuple[int, Dict[str, Sequence[float]], Dict[str, Sequence[float]]]],
) -> bpy.types.Action:
    """keys: (frame, {bone: euler degrees in avatar space}, {bone: offset in meters in avatar space})."""
    rig.animation_data_create()
    act = bpy.data.actions.new(name)
    rig.animation_data.action = act
    bones = sorted({b for _f, rot, loc in keys for b in list(rot) + list(loc)})
    for frame, rot, loc in keys:
        for bone in bones:
            pb = rig.pose.bones[bone]
            q = pose_quaternion(rig, bone, rot.get(bone, (0, 0, 0)))
            pb.rotation_quaternion = Quaternion([round(c, 6) for c in q])
            pb.keyframe_insert("rotation_quaternion", frame=frame, group=bone)
            off = Vector(loc.get(bone, (0, 0, 0)))
            local = rest_rotation(rig, bone).inverted() @ off
            pb.location = Vector([round(c, 6) for c in local])
            pb.keyframe_insert("location", frame=frame, group=bone)
    scene = bpy.context.scene
    scene.frame_start = 0
    scene.frame_end = max(f for f, _r, _l in keys)
    return act


# ---------------------------------------------------------------- export

def export_glb(path: str, animations: bool = False) -> None:
    bpy.ops.export_scene.gltf(
        filepath=path,
        export_format="GLB",
        export_copyright="VYBE (code-built art, assets/LICENSES.md)",
        export_yup=True,
        export_apply=False,
        export_texcoords=False,
        export_normals=False,
        export_tangents=False,
        export_materials="EXPORT",
        export_vertex_color="NONE",
        export_skins=True,
        export_all_influences=False,
        export_morph=True,
        export_morph_normal=False,
        export_animations=animations,
        export_animation_mode="ACTIONS",
        export_force_sampling=True,
        export_frame_step=1,
        export_optimize_animation_size=True,
        export_def_bones=False,
        export_extras=False,
        export_cameras=False,
        export_lights=False,
        will_save_settings=False,
    )


# ---------------------------------------------------------------- thumbnails

LIGHT = Vector((-0.45, -0.75, 0.6)).normalized()


def toon_shading(obj: bpy.types.Object) -> None:
    """Swap the export materials for a flat cel look (the same three bands as the web viewport)."""
    for slot in obj.material_slots:
        src = slot.material
        name = src.name + "__thumb"
        mat = bpy.data.materials.get(name)
        if mat is None:
            mat = bpy.data.materials.new(name)
            mat.use_nodes = True
            nt = mat.node_tree
            for n in list(nt.nodes):
                nt.nodes.remove(n)
            base = tuple(src.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value)
            emit = tuple(src.node_tree.nodes["Principled BSDF"].inputs["Emission Color"].default_value)
            emit_strength = src.node_tree.nodes["Principled BSDF"].inputs["Emission Strength"].default_value
            geo = nt.nodes.new("ShaderNodeNewGeometry")
            d = nt.nodes.new("ShaderNodeVectorMath")
            d.operation = "DOT_PRODUCT"
            d.inputs[1].default_value = LIGHT
            ramp = nt.nodes.new("ShaderNodeValToRGB")
            ramp.color_ramp.interpolation = "CONSTANT"
            els = ramp.color_ramp.elements
            els[0].position = 0.0
            els[0].color = (0.5, 0.5, 0.5, 1)
            els[1].position = 0.15
            els[1].color = (0.78, 0.78, 0.78, 1)
            e = els.new(0.55)
            e.color = (1, 1, 1, 1)
            mix = nt.nodes.new("ShaderNodeMix")
            mix.data_type = "RGBA"
            mix.blend_type = "MULTIPLY"
            mix.inputs[0].default_value = 1.0
            mix.inputs[6].default_value = base
            add = nt.nodes.new("ShaderNodeMix")
            add.data_type = "RGBA"
            add.blend_type = "ADD"
            add.inputs[0].default_value = 1.0
            add.inputs[7].default_value = (emit[0] * emit_strength, emit[1] * emit_strength, emit[2] * emit_strength, 1)
            em = nt.nodes.new("ShaderNodeEmission")
            out = nt.nodes.new("ShaderNodeOutputMaterial")
            nt.links.new(geo.outputs["Normal"], d.inputs[0])
            nt.links.new(d.outputs["Value"], ramp.inputs["Fac"])
            nt.links.new(ramp.outputs["Color"], mix.inputs[7])
            nt.links.new(mix.outputs[2], add.inputs[6])
            nt.links.new(add.outputs[2], em.inputs["Color"])
            nt.links.new(em.outputs["Emission"], out.inputs["Surface"])
        slot.material = mat


def recolor(obj: bpy.types.Object, colors: Dict[str, str]) -> None:
    """Thumbnail of a color variant: the export material of the same name gets another base color."""
    for slot in obj.material_slots:
        role = slot.material.name.split(".")[0]
        if role in colors:
            copy = slot.material.copy()
            copy.name = role + "__variant"
            r, g, b = hex_linear(colors[role])
            copy.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (r, g, b, 1)
            slot.material = copy


def render_thumbnail(path: str, objects: Iterable[bpy.types.Object], size: int = 256, yaw: float = -28.0,
                     pitch: float = -12.0, line: float = 1.4) -> None:
    """Orthographic three-quarter view of the given (posed) objects on a transparent background."""
    objs = list(objects)
    bpy.context.view_layer.update()
    depsgraph = bpy.context.evaluated_depsgraph_get()
    rot = Matrix.Rotation(math.radians(yaw), 4, "Z") @ Matrix.Rotation(math.radians(pitch), 4, "X")
    inv = rot.inverted()
    lo = Vector((1e9, 1e9, 1e9))
    hi = Vector((-1e9, -1e9, -1e9))
    for obj in objs:
        ev = obj.evaluated_get(depsgraph)
        me = ev.to_mesh()
        for v in me.vertices:
            p = inv @ (ev.matrix_world @ v.co)
            lo = Vector(map(min, lo, p))
            hi = Vector(map(max, hi, p))
        ev.to_mesh_clear()
    center = rot @ ((lo + hi) / 2)
    extent = max(hi.x - lo.x, hi.z - lo.z)

    scene = bpy.context.scene
    cam_data = bpy.data.cameras.new("thumb")
    cam_data.type = "ORTHO"
    cam_data.ortho_scale = extent * 1.18
    cam = bpy.data.objects.new("thumb", cam_data)
    scene.collection.objects.link(cam)
    forward = rot @ Vector((0, 1, 0))
    cam.location = center - forward * 5
    cam.rotation_euler = (rot @ Matrix.Rotation(math.radians(90), 4, "X")).to_euler()
    scene.camera = cam

    for obj in objs:
        toon_shading(obj)
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 16
    scene.cycles.use_denoising = False
    scene.cycles.seed = 0
    scene.cycles.max_bounces = 0
    scene.render.film_transparent = True
    scene.render.resolution_x = size
    scene.render.resolution_y = size
    scene.render.resolution_percentage = 100
    scene.render.threads_mode = "FIXED"
    scene.render.threads = 1
    scene.render.use_freestyle = True
    scene.render.line_thickness_mode = "ABSOLUTE"
    scene.render.line_thickness = line
    lineset = scene.view_layers[0].freestyle_settings.linesets
    if len(lineset) == 0:
        lineset.new("outline")
    ls = lineset[0]
    ls.select_by_visibility = True
    ls.select_silhouette = True
    ls.select_border = True
    ls.select_crease = False
    if ls.linestyle is None:
        ls.linestyle = bpy.data.linestyles.new("outline")
    ls.linestyle.color = (0.06, 0.07, 0.12)
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "None"
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.image_settings.compression = 100
    scene.render.use_stamp = False
    scene.render.filepath = path
    world = bpy.data.worlds.new("thumb")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.0
    scene.world = world
    bpy.ops.render.render(write_still=True)


def hide(objs: Iterable[bpy.types.Object]) -> None:
    for o in objs:
        o.hide_render = True
        o.hide_set(True)


def pose_at(rig: bpy.types.Object, frame: int) -> None:
    bpy.context.scene.frame_set(frame)


def objects_named(names: Sequence[str]) -> List[bpy.types.Object]:
    return [bpy.data.objects[n] for n in names]
