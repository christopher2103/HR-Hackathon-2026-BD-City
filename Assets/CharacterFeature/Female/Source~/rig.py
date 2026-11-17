"""Adds a Unity-Humanoid-friendly armature (Mixamo bone names), skin weights, and exports.
Run: python rig.py
"""
import bpy, math, os, sys
from mathutils import Vector
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import build

OUT = build.OUT

# name: (head, tail, parent, influence radius)
BONES = {
    'Hips': ((0, 0, 0.76), (0, 0, 0.86), None, 0.13),
    'Spine': ((0, 0, 0.86), (0, 0, 0.96), 'Hips', 0.13),
    'Spine1': ((0, 0, 0.96), (0, 0, 1.08), 'Spine', 0.13),
    'Neck': ((0, 0, 1.08), (0, 0, 1.17), 'Spine1', 0.045),
    'Head': ((0, 0, 1.17), (0, 0, 1.50), 'Neck', 0.17),
}
for side, s in (('Left', 1), ('Right', -1)):
    BONES.update({
        f'{side}Shoulder': ((s * 0.03, 0, 1.08), (s * 0.15, 0, 1.07), 'Spine1', 0.05),
        f'{side}Arm': ((s * 0.15, 0, 1.07), (s * 0.38, 0, 1.07), f'{side}Shoulder', 0.055),
        f'{side}ForeArm': ((s * 0.38, 0, 1.07), (s * 0.575, 0, 1.07), f'{side}Arm', 0.05),
        f'{side}Hand': ((s * 0.575, 0, 1.07), (s * 0.70, 0, 1.07), f'{side}ForeArm', 0.05),
        f'{side}UpLeg': ((s * 0.085, 0, 0.74), (s * 0.09, 0, 0.40), 'Hips', 0.075),
        f'{side}Leg': ((s * 0.09, 0, 0.40), (s * 0.09, 0, 0.09), f'{side}UpLeg', 0.055),
        f'{side}Foot': ((s * 0.09, 0.0, 0.09), (s * 0.09, -0.07, 0.035), f'{side}Leg', 0.05),
        f'{side}ToeBase': ((s * 0.09, -0.07, 0.035), (s * 0.09, -0.15, 0.035), f'{side}Foot', 0.045),
    })

ALPHA = 0.2   # how much radial distance counts vs distance along the bone
POWER = 4


def build_armature():
    arm = bpy.data.armatures.new('Armature')
    ob = bpy.data.objects.new('Armature', arm)
    bpy.context.scene.collection.objects.link(ob)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.mode_set(mode='EDIT')
    for name, (h, t, p, _) in BONES.items():
        b = arm.edit_bones.new(name)
        b.head, b.tail = h, t
        b.roll = 0
    for name, (h, t, p, _) in BONES.items():
        if p:
            eb = arm.edit_bones[name]
            eb.parent = arm.edit_bones[p]
            eb.use_connect = (Vector(arm.edit_bones[p].tail) - Vector(h)).length < 1e-4
    bpy.ops.object.mode_set(mode='OBJECT')
    arm.display_type = 'STICK'
    return ob


def seg_dist(p, a, b):
    ab = b - a
    L = ab.length
    d = ab / L
    t = (p - a).dot(d)
    out = -t if t < 0 else (t - L if t > L else 0.0)
    radial = ((p - a) - d * t).length
    return math.sqrt(out * out + (ALPHA * radial) ** 2)


def allowed(name, co, head_only=False):
    if head_only:
        return name == 'Head'
    if co.x > 0.006 and name.startswith('Right'):
        return False
    if co.x < -0.006 and name.startswith('Left'):
        return False
    if co.z < 0.10 and abs(co.x) > 0.03:
        return name.endswith('Foot') or name.endswith('ToeBase')
    return True


def weight_object(obj, arm_ob, head_only=False):
    obj.vertex_groups.clear()
    groups = {n: obj.vertex_groups.new(name=n) for n in BONES}
    segs = {n: (Vector(h), Vector(t), r) for n, (h, t, p, r) in BONES.items()}
    for v in obj.data.vertices:
        co = v.co
        ws = []
        for n, (a, b, r) in segs.items():
            if not allowed(n, co, head_only):
                continue
            d = seg_dist(co, a, b) / r
            ws.append((1.0 / (d ** POWER + 1e-6), n))
        ws.sort(reverse=True)
        ws = ws[:4]
        tot = sum(w for w, _ in ws)
        ws = [(w / tot, n) for w, n in ws if w / tot > 0.02]
        tot = sum(w for w, _ in ws)
        for w, n in ws:
            groups[n].add([v.index], w / tot, 'REPLACE')
    obj.parent = arm_ob
    mod = obj.modifiers.new('Armature', 'ARMATURE')
    mod.object = arm_ob


def main():
    objs = build.build_all()
    arm_ob = build_armature()
    for name, o in objs.items():
        weight_object(o, arm_ob, head_only=(name == 'Hair'))
    # face features belong to the head rigidly: re-weight verts above the chin on Body
    body = objs['Body']
    head_vg = body.vertex_groups['Head']
    for v in body.data.vertices:
        if v.co.z > 1.19 and abs(v.co.x) < 0.2:
            for g in list(v.groups):
                body.vertex_groups[g.group].remove([v.index])
            head_vg.add([v.index], 1.0, 'REPLACE')
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT, 'BD_Female_Avatar.blend'))

    bpy.ops.object.select_all(action='DESELECT')
    for o in list(objs.values()) + [arm_ob]:
        o.select_set(True)
    bpy.ops.export_scene.fbx(
        filepath=os.path.join(OUT, 'BD_Female_Avatar.fbx'), use_selection=True,
        object_types={'ARMATURE', 'MESH'}, apply_unit_scale=True, apply_scale_options='FBX_SCALE_ALL',
        axis_forward='-Z', axis_up='Y', bake_space_transform=True, add_leaf_bones=False,
        primary_bone_axis='Y', secondary_bone_axis='X', use_armature_deform_only=True,
        mesh_smooth_type='FACE', bake_anim=False, path_mode='AUTO')
    total = 0
    for k, o in objs.items():
        tris = sum(len(p.vertices) - 2 for p in o.data.polygons)
        total += tris
        print(f'{k}: {tris} tris')
    print('total', total)


if __name__ == '__main__':
    main()
