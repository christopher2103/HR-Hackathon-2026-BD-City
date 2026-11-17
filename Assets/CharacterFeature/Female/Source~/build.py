"""Builds the stylized female avatar (from the Immersive Surprise poster) in Blender.

Objects: Body (skin + face), Hair, Jacket, Pants, Shoes, parented to a Humanoid armature.
Run:  python build.py  [--render]
"""
import bpy, bmesh, math, random, sys, os
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

OUT = os.path.dirname(os.path.abspath(__file__))
random.seed(7)

# ---------------------------------------------------------------- utilities

def srgb(c):
    def f(x):
        return x / 12.92 if x <= 0.04045 else ((x + 0.055) / 1.055) ** 2.4
    return tuple(f(v) for v in c)


def hexcol(h):
    h = h.lstrip('#')
    return tuple(int(h[i:i + 2], 16) / 255 for i in (0, 2, 4))


MATS = {}


def mat(name, hexc, rough=0.6, metal=0.0, spec=0.5):
    if name in MATS:
        return MATS[name]
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes.get('Principled BSDF')
    b.inputs['Base Color'].default_value = (*srgb(hexcol(hexc)), 1)
    b.inputs['Roughness'].default_value = rough
    b.inputs['Metallic'].default_value = metal
    m.diffuse_color = (*srgb(hexcol(hexc)), 1)
    m["unity_hex"] = hexc
    MATS[name] = m
    return m


def smoothstep(e0, e1, x):
    t = max(0.0, min(1.0, (x - e0) / (e1 - e0)))
    return t * t * (3 - 2 * t)


def link(obj):
    bpy.context.scene.collection.objects.link(obj)
    return obj


def bake_obj(obj, name=None):
    """Return a new object with all modifiers applied (works for meshes, curves, metaballs)."""
    dg = bpy.context.evaluated_depsgraph_get()
    me = bpy.data.meshes.new_from_object(obj.evaluated_get(dg))
    new = bpy.data.objects.new(name or obj.name, me)
    new.matrix_world = obj.matrix_world.copy()
    link(new)
    bpy.data.objects.remove(obj)
    if name:
        new.name = name
    return new


def apply_transform(obj):
    obj.data.transform(obj.matrix_world)
    obj.matrix_world = Matrix.Identity(4)


def skin_mesh(name, verts, edges, radii, root=0, subsurf=2):
    me = bpy.data.meshes.new(name)
    me.from_pydata([Vector(v) for v in verts], edges, [])
    bm = bmesh.new()
    bm.from_mesh(me)
    lay = bm.verts.layers.skin.verify()
    bm.verts.ensure_lookup_table()
    for v, r in zip(bm.verts, radii):
        rr = (r, r) if isinstance(r, (int, float)) else r
        v[lay].radius = rr
        v[lay].use_root = False
    bm.verts[root][lay].use_root = True
    bm.to_mesh(me)
    bm.free()
    obj = link(bpy.data.objects.new(name, me))
    m = obj.modifiers.new('Skin', 'SKIN')
    m.use_smooth_shade = True
    m.branch_smoothing = 0.6
    if subsurf:
        s = obj.modifiers.new('Sub', 'SUBSURF')
        s.levels = s.render_levels = subsurf
    return bake_obj(obj, name)


def ellipsoid(name, center, radii, segs=24, rings=12, rot=None):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=segs, v_segments=rings, radius=1.0)
    S = Matrix.Diagonal((*radii, 1))
    R = rot.to_4x4() if rot is not None else Matrix.Identity(4)
    bm.transform(Matrix.Translation(center) @ R @ S)
    for f in bm.faces:
        f.smooth = True
    bm.to_mesh(me)
    bm.free()
    return link(bpy.data.objects.new(name, me))


def tube_from_points(name, pts, radius, res=4):
    cu = bpy.data.curves.new(name, 'CURVE')
    cu.dimensions = '3D'
    cu.bevel_depth = radius
    cu.bevel_resolution = res
    cu.use_fill_caps = True
    sp = cu.splines.new('POLY')
    sp.points.add(len(pts) - 1)
    for p, c in zip(sp.points, pts):
        p.co = (*c, 1)
    ob = link(bpy.data.objects.new(name, cu))
    ob = bake_obj(ob, name)
    for p in ob.data.polygons:
        p.use_smooth = True
    return ob


def set_material(obj, m):
    obj.data.materials.clear()
    obj.data.materials.append(m)


def join(objs, name):
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    o = bpy.context.view_layer.objects.active
    o.name = name
    o.data.name = name
    return o


def flatten_torso(obj, factor=0.74):
    """Torso is deeper than wide in a skin-modifier mesh: squash it front-to-back."""
    for v in obj.data.vertices:
        x, y, z = v.co
        m = (1 - smoothstep(0.11, 0.19, abs(x))) * smoothstep(0.66, 0.78, z)
        v.co.y = y * (1 - (1 - factor) * m)


# ---------------------------------------------------------------- dimensions (metres, Blender Z-up, facing -Y)
HEAD_C = Vector((0, 0, 1.315))
HEAD_R = Vector((0.155, 0.145, 0.168))
SH_Z = 1.07       # shoulder / arm height (T-pose)
HIP_X = 0.085


def head_front_y(x, z, inset=0.0):
    t = 1 - (x / HEAD_R.x) ** 2 - ((z - HEAD_C.z) / HEAD_R.z) ** 2
    return -HEAD_R.y * math.sqrt(max(t, 0.0)) + inset


# ---------------------------------------------------------------- materials
def init_mats():
    global M_SKIN, M_HAIR, M_EYE, M_WHITE, M_BROW, M_MOUTH, M_JK_BLACK, M_JK_MINT, M_ZIP, M_PANTS, M_STRIPE, M_SHOE, M_SOLE, M_LACE
    M_SKIN = mat('Skin', '#F3C7AA', 0.55)
    M_HAIR = mat('Hair_Red', '#F2383A', 0.55)
    M_EYE = mat('Eye_Black', '#111111', 0.15)
    M_WHITE = mat('Eye_Highlight', '#FFFFFF', 0.2)
    M_BROW = mat('Brow_Red', '#E8343A', 0.6)
    M_MOUTH = mat('Mouth', '#8A5546', 0.6)
    M_JK_BLACK = mat('Jacket_Black', '#2E2E2E', 0.75)
    M_JK_MINT = mat('Jacket_Mint', '#9FE6CF', 0.7)
    M_ZIP = mat('Zipper_Silver', '#BFBFBF', 0.35, metal=0.6)
    M_PANTS = mat('Pants_Black', '#2B2B2B', 0.75)
    M_STRIPE = mat('Pants_Stripe_White', '#F2F2F2', 0.6)
    M_SHOE = mat('Shoe_Pink', '#F6A3C0', 0.5)
    M_SOLE = mat('Shoe_Sole_Black', '#1E1E1E', 0.6)
    M_LACE = mat('Shoe_Lace_White', '#FAFAFA', 0.5)


# ---------------------------------------------------------------- body (skin)
def build_body():
    V, E, R = [], [], []

    def add(p, r, parent=None):
        V.append(p); R.append(r)
        i = len(V) - 1
        if parent is not None:
            E.append((parent, i))
        return i

    pelvis = add((0, 0, 0.78), 0.105)
    waist = add((0, 0, 0.88), 0.095, pelvis)
    chest = add((0, 0, 0.99), 0.112, waist)
    upper = add((0, 0, SH_Z), 0.10, chest)
    nb = add((0, 0, 1.11), 0.043, upper)
    nt = add((0, 0, 1.20), 0.041, nb)
    for s in (1, -1):
        sh = add((s * 0.15, 0, SH_Z), 0.048, upper)
        el = add((s * 0.38, 0, SH_Z), 0.039, sh)
        wr = add((s * 0.575, 0, SH_Z), 0.031, el)
        hp = add((s * HIP_X, 0, 0.72), 0.062, pelvis)
        kn = add((s * 0.09, 0, 0.40), 0.041, hp)
        an = add((s * 0.09, 0, 0.10), 0.032, kn)
        hl = add((s * 0.09, 0.005, 0.045), 0.03, an)
        to = add((s * 0.09, -0.09, 0.035), 0.028, hl)
    body = skin_mesh('BodySkin', V, E, R, root=pelvis, subsurf=2)
    flatten_torso(body, 0.76)

    parts = [body]
    head = ellipsoid('Head', HEAD_C, HEAD_R, segs=32, rings=18)
    # fuller cheeks / softer chin
    for v in head.data.vertices:
        rel = (v.co.z - HEAD_C.z) / HEAD_R.z
        if rel < 0:
            k = 1 + 0.06 * math.sin(math.pi * min(1, -rel * 1.4))
            v.co.x *= k
    parts.append(head)
    for s in (1, -1):
        parts.append(ellipsoid('Ear', (s * 0.152, 0.012, 1.29), (0.016, 0.03, 0.036), 12, 8))
        # mitten hand, palm down in T-pose + thumb
        parts.append(ellipsoid('Hand', (s * 0.645, 0, SH_Z), (0.072, 0.05, 0.024), 16, 10))
        rot = Matrix.Rotation(math.radians(-s * 45), 3, 'Z')
        parts.append(ellipsoid('Thumb', (s * 0.61, -0.05, SH_Z - 0.003), (0.034, 0.014, 0.015), 10, 6, rot))
    for p in parts:
        set_material(p, M_SKIN)

    # nose
    nz = 1.262
    nose = ellipsoid('Nose', (0, head_front_y(0, nz, 0.006), nz), (0.018, 0.02, 0.021), 14, 10)
    set_material(nose, M_SKIN)
    parts.append(nose)

    face = []
    for s in (1, -1):
        ex, ez = s * 0.06, 1.298
        e = ellipsoid('Eye', (ex, head_front_y(ex, ez, 0.0035), ez), (0.0125, 0.008, 0.019), 14, 10)
        set_material(e, M_EYE); face.append(e)
        hx, hz = ex - s * 0.004, ez + 0.009
        h = ellipsoid('Highlight', (hx, head_front_y(hx, hz, -0.0035), hz), (0.0035, 0.002, 0.0035), 8, 6)
        set_material(h, M_WHITE); face.append(h)
        # lash flick at outer top corner
        lx, lz = ex + s * 0.012, ez + 0.012
        rot = Matrix.Rotation(math.radians(s * 35), 3, 'Y')
        l = ellipsoid('Lash', (lx, head_front_y(lx, lz, 0.0005), lz), (0.007, 0.003, 0.0022), 8, 6, rot)
        set_material(l, M_EYE); face.append(l)
        # eyebrow arc
        pts = []
        for i in range(9):
            t = i / 8
            x = s * (0.028 + 0.075 * t)
            z = 1.352 + 0.014 * math.sin(math.pi * (0.15 + 0.75 * t)) - 0.004 * t
            pts.append((x, head_front_y(x, z, -0.0015), z))
        b = tube_from_points('Brow', pts, 0.0055)
        set_material(b, M_BROW); face.append(b)
    pts = []
    for i in range(9):
        x = -0.032 + 0.064 * i / 8
        z = 1.217 + 6.0 * x * x
        pts.append((x, head_front_y(x, z, -0.0008), z))
    mo = tube_from_points('Mouth', pts, 0.0018)
    set_material(mo, M_MOUTH); face.append(mo)

    body = join(parts + face, 'Body')
    return body


# ---------------------------------------------------------------- hair (metaballs -> mesh)
def build_hair():
    mb = bpy.data.metaballs.new('HairMB')
    mb.resolution = 0.011
    mb.render_resolution = 0.011
    mb.threshold = 0.6
    ob = link(bpy.data.objects.new('HairMB', mb))

    def ball(co, r, stiff=2.0):
        el = mb.elements.new()
        el.co = co
        el.radius = r
        el.stiffness = stiff

    # inner filler so the curls sit on a solid volume
    ball((0, 0.035, 1.37), 0.19, 2.0)
    ball((0, 0.075, 1.27), 0.15, 2.0)

    C = Vector((0, 0.02, 1.335))
    Rr = Vector((0.188, 0.182, 0.198))
    n = 170
    for i in range(n):
        yv = 1 - 2 * (i + 0.5) / n
        r = math.sqrt(1 - yv * yv)
        th = math.pi * (3 - math.sqrt(5)) * i
        d = Vector((math.cos(th) * r, math.sin(th) * r, yv))   # unit dir (x,y,z)
        p = Vector((C.x + Rr.x * d.x, C.y + Rr.y * d.y, C.z + Rr.z * d.z))
        if p.z < 1.10:
            continue
        if d.y < -0.15 and p.z < 1.425 and abs(p.x) < 0.15:   # face window
            continue
        if d.y < -0.35 and p.z < 1.25:                         # keep cheeks/jaw free
            continue
        if d.y < 0.1 and p.z < 1.16:
            continue
        ball(p, random.uniform(0.05, 0.068), random.uniform(1.6, 2.4))

    # bangs: curls along the hairline, one hanging over the forehead
    for x in (-0.11, -0.06, 0.0, 0.055, 0.105):
        z = 1.43 + random.uniform(-0.01, 0.01)
        ball((x, head_front_y(x, z, -0.012), z), 0.048)
    ball((-0.03, head_front_y(-0.03, 1.40, -0.016), 1.40), 0.032, 2.5)
    # extra volume falling on her left side (image right)
    for p in ((0.19, 0.03, 1.15), (0.17, 0.09, 1.12), (0.205, -0.03, 1.21), (0.14, 0.12, 1.10)):
        ball(p, 0.06)
    for p in ((-0.19, 0.05, 1.17), (-0.15, 0.11, 1.13)):
        ball(p, 0.055)

    hair = bake_obj(ob, 'Hair')
    # curly grooves + polycount
    tex = bpy.data.textures.new('HairNoise', 'CLOUDS')
    tex.noise_scale = 0.022
    tex.noise_depth = 1
    d = hair.modifiers.new('Disp', 'DISPLACE')
    d.texture = tex
    d.strength = 0.016
    d.mid_level = 0.5
    dec = hair.modifiers.new('Dec', 'DECIMATE')
    dec.ratio = 0.35
    hair = bake_obj(hair, 'Hair')
    for p in hair.data.polygons:
        p.use_smooth = True
    set_material(hair, M_HAIR)
    return hair


# ---------------------------------------------------------------- jacket
def build_jacket():
    V, E, R = [], [], []

    def add(p, r, parent=None):
        V.append(p); R.append(r)
        i = len(V) - 1
        if parent is not None:
            E.append((parent, i))
        return i

    hem = add((0, 0, 0.755), 0.132)
    waist = add((0, 0, 0.88), 0.126, hem)
    chest = add((0, 0, 0.99), 0.142, waist)
    upper = add((0, 0, SH_Z), 0.128, chest)
    cb = add((0, 0, 1.115), 0.060, upper)
    ct = add((0, 0, 1.175), 0.057, cb)
    for s in (1, -1):
        sh = add((s * 0.16, 0, SH_Z), 0.066, upper)
        el = add((s * 0.38, 0, SH_Z), 0.058, sh)
        cf = add((s * 0.545, 0, SH_Z), 0.052, el)
        cu = add((s * 0.575, 0, SH_Z), 0.045, cf)
    j = skin_mesh('Jacket', V, E, R, root=hem, subsurf=2)
    flatten_torso(j, 0.74)

    # crisp colour-block edges: cut along the band boundary planes
    bm = bmesh.new()
    bm.from_mesh(j.data)
    cuts = [
        ((0, 0, 0.885), (-0.85, 0, 1)), ((0, 0, 0.885), (0.85, 0, 1)),     # lower edge of mint V
        ((0, 0, 1.015), (-1.25, 0, 1)), ((0, 0, 1.015), (1.25, 0, 1)),     # black collar triangle
    ]
    for co, no in cuts:
        geom = bm.verts[:] + bm.edges[:] + bm.faces[:]
        bmesh.ops.bisect_plane(bm, geom=geom, plane_co=co, plane_no=Vector(no).normalized())
    j.data.materials.clear()
    j.data.materials.append(M_JK_BLACK)
    j.data.materials.append(M_JK_MINT)
    for f in bm.faces:
        c = f.calc_center_median()
        ax = abs(c.x)
        mint = c.z > 0.885 + 0.85 * ax and not (c.z > 1.015 + 1.25 * ax) and ax < 0.40
        if c.z > 1.10 and ax < 0.075:
            mint = False
        f.material_index = 1 if mint else 0
        f.smooth = True
    bm.to_mesh(j.data)
    bm.free()

    # zipper down the front centre (raycast to follow the surface)
    bm = bmesh.new(); bm.from_mesh(j.data)
    tree = BVHTree.FromBMesh(bm)
    pts = []
    z = 0.765
    while z < 1.17:
        hit = tree.ray_cast(Vector((0, -1, z)), Vector((0, 1, 0)))
        if hit[0] is not None:
            pts.append((0, hit[0].y + 0.0015, z))
        z += 0.012
    bm.free()
    zp = tube_from_points('Zipper', pts, 0.0035, res=2)
    set_material(zp, M_ZIP)
    return join([j, zp], 'Jacket')


# ---------------------------------------------------------------- pants
def build_pants():
    V, E, R = [], [], []

    def add(p, r, parent=None):
        V.append(p); R.append(r)
        i = len(V) - 1
        if parent is not None:
            E.append((parent, i))
        return i

    pel = add((0, 0, 0.78), 0.122)
    wa = add((0, 0, 0.87), 0.112, pel)
    for s in (1, -1):
        hp = add((s * HIP_X, 0, 0.70), 0.077, pel)
        kn = add((s * 0.09, 0, 0.40), 0.054, hp)
        an = add((s * 0.09, 0, 0.11), 0.047, kn)
    p = skin_mesh('Pants', V, E, R, root=pel, subsurf=2)
    flatten_torso(p, 0.8)
    set_material(p, M_PANTS)

    bm = bmesh.new(); bm.from_mesh(p.data)
    tree = BVHTree.FromBMesh(bm)
    stripes = []
    for s in (1, -1):
        pts = []
        z = 0.12
        while z < 0.74:
            hit = tree.ray_cast(Vector((s * 1.0, 0, z)), Vector((-s, 0, 0)))
            if hit[0] is not None:
                pts.append((hit[0].x - s * 0.0012, 0, z))
            z += 0.02
        st = tube_from_points('Stripe', pts, 0.0042, res=2)
        set_material(st, M_STRIPE)
        stripes.append(st)
    bm.free()
    return join([p] + stripes, 'Pants')


# ---------------------------------------------------------------- shoes
def build_shoes():
    objs = []
    for s in (1, -1):
        x = s * 0.09
        V = [(x, 0.05, 0.065), (x, -0.02, 0.066), (x, -0.09, 0.057), (x, -0.155, 0.05), (x, 0.01, 0.125)]
        E = [(0, 1), (1, 2), (2, 3), (1, 4)]
        R = [(0.058, 0.058), (0.064, 0.064), (0.061, 0.061), (0.05, 0.05), (0.052, 0.052)]
        sh = skin_mesh('Shoe', V, E, R, root=1, subsurf=2)
        # flat bottom, slight squash
        for v in sh.data.vertices:
            if v.co.z < 0.0:
                v.co.z = 0.0
            if v.co.z > 0.15:
                v.co.z = 0.15 + (v.co.z - 0.15) * 0.3
        sh.data.materials.clear()
        sh.data.materials.append(M_SHOE)
        sh.data.materials.append(M_SOLE)
        bm = bmesh.new(); bm.from_mesh(sh.data)
        bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:],
                               plane_co=(0, 0, 0.014), plane_no=(0, 0, 1))
        for f in bm.faces:
            f.material_index = 1 if f.calc_center_median().z < 0.014 else 0
            f.smooth = True
        tree = BVHTree.FromBMesh(bm)
        laces = []
        for k, yy in enumerate((-0.035, -0.057, -0.079, -0.101)):
            hit = tree.ray_cast(Vector((x, yy, 1.0)), Vector((0, 0, -1)))
            if hit[0] is None:
                continue
            zz = hit[0].z
            w = 0.034 - k * 0.002
            pts = [(x - w, yy, zz - 0.006), (x - w * 0.5, yy, zz + 0.002), (x, yy, zz + 0.0035),
                   (x + w * 0.5, yy, zz + 0.002), (x + w, yy, zz - 0.006)]
            l = tube_from_points('Lace', pts, 0.0035, res=2)
            set_material(l, M_LACE)
            laces.append(l)
        bm.to_mesh(sh.data); bm.free()
        objs.append(sh)
        objs.extend(laces)
    return join(objs, 'Shoes')


# ---------------------------------------------------------------- scene
def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    MATS.clear()


def build_all():
    reset()
    init_mats()
    body = build_body()
    hair = build_hair()
    jacket = build_jacket()
    pants = build_pants()
    shoes = build_shoes()
    return dict(Body=body, Hair=hair, Jacket=jacket, Pants=pants, Shoes=shoes)


if __name__ == '__main__':
    objs = build_all()
    for k, o in objs.items():
        tris = sum(len(p.vertices) - 2 for p in o.data.polygons)
        print(f'{k}: {tris} tris')
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT, 'geo.blend'))
