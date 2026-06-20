import bpy
import bmesh
import os
from mathutils import Vector
from collections import defaultdict

# Number of columns (along Blender X axis) in the output grid.
GRID_X = 8
# Number of rows (along Blender Y axis) in the output grid.
GRID_Y = 8

# Objects whose collection name contains any of these keywords are always
# placed into a chunk as a whole unit, even if their bounding box is larger
# than one chunk. Use this to protect multi-story buildings or other assets
# that would look broken if cut by a bisect plane.
KEEP_WHOLE_KEYWORDS = ['Buildings']

# An object whose bounding box along X or Y exceeds chunk_size * this ratio
# enters the slice pipeline and is cut across chunk boundaries with bmesh.
# 0.99 means "slice anything that doesn't fit inside a single chunk".
SLICE_SIZE_RATIO = 0.99

# Destination folder for exported FBX files.
# The "//" prefix makes the path relative to the current .blend file.
OUTPUT_DIR = bpy.path.abspath("//chunks_export/")

# When True, the _chunks collection and all previously generated slice parts
# are removed before the script runs, keeping the scene clean on re-runs.
CLEAR_PREVIOUS = True

# Merge vertices closer than 0.0001 m before slicing to avoid t-junctions
# and seam artifacts along chunk boundaries.
MERGE_DOUBLES = True

# Set to False to skip FBX export and only build the _chunks collections
# inside Blender (useful for a quick visual check without writing files).
DO_EXPORT = True

# Logging
# OUTPUT_DIR is created here unconditionally so the log file is always written,
# even when DO_EXPORT is False (dry-run / visual-check mode).
os.makedirs(OUTPUT_DIR, exist_ok=True)
_log_path = os.path.join(OUTPUT_DIR, "chunks_export.log")
_log_file = open(_log_path, "w", encoding="utf-8")

def log(msg=""):
    _log_file.write(msg + "\n")
    _log_file.flush()

# Collect all selected mesh objects. The script ignores cameras, lights, etc.
selected = [o for o in bpy.context.selected_objects if o.type == 'MESH']
if not selected:
    _log_file.close()
    raise Exception("Select mesh objects first (press A in the viewport)")

log(f"Selected mesh objects: {len(selected)}")
log(f"Grid: {GRID_X} × {GRID_Y}")

# ── Step 1: compute the combined world-space bounding box of all selected objects ──
# We iterate every corner of every object's local bounding box, transform it
# to world space with matrix_world, and track the global min/max.
all_min = Vector((float('inf'),) * 3)
all_max = Vector((float('-inf'),) * 3)
for o in selected:
    for c in o.bound_box:
        wc = o.matrix_world @ Vector(c)
        all_min = Vector((min(all_min[i], wc[i]) for i in range(3)))
        all_max = Vector((max(all_max[i], wc[i]) for i in range(3)))

# Derive per-chunk dimensions from the total span divided by the grid size.
span_x = all_max.x - all_min.x
span_y = all_max.y - all_min.y

if span_x == 0 or span_y == 0:
    _log_file.close()
    raise Exception(
        f"Bounding box has zero span (X={span_x:.4f}, Y={span_y:.4f}). "
        "Make sure the selected objects have real geometry and are not "
        "collapsed to a single point or a flat line."
    )

chunk_w = span_x / GRID_X   # width of one chunk in Blender units (meters)
chunk_h = span_y / GRID_Y   # height of one chunk in Blender units (meters)

log(f"World bbox X: [{all_min.x:.1f} .. {all_max.x:.1f}]  span {span_x:.1f}m")
log(f"World bbox Y: [{all_min.y:.1f} .. {all_max.y:.1f}]  span {span_y:.1f}m")
log(f"Chunk size:   {chunk_w:.2f}m × {chunk_h:.2f}m")


def world_to_chunk(x, y):
    """Return the (col, row) grid index for a world-space XY position.

    Clamps the result so points exactly on the far edge land in the last cell
    rather than an out-of-range index.
    """
    cx = int((x - all_min.x) / chunk_w)
    cy = int((y - all_min.y) / chunk_h)
    return (max(0, min(GRID_X - 1, cx)),
            max(0, min(GRID_Y - 1, cy)))


def chunk_pivot(cx, cy):
    """Return the world-space center of chunk (cx, cy) at Z = 0.

    This is the point used as the FBX export origin (each chunk's objects are
    shifted by -pivot before export), and the value Unity stores in the scene
    root's world position.
    """
    return Vector((all_min.x + (cx + 0.5) * chunk_w,
                   all_min.y + (cy + 0.5) * chunk_h,
                   0.0))


# ── Step 2: categorise objects into "slice" or "group whole" ──
# Objects are routed to the slice pipeline only when they are too large to fit
# in a single chunk AND they don't belong to a protected collection.
to_slice, to_group = [], []
for o in selected:
    # Join collection names into one string for a simple substring check.
    coll_names = ' '.join(c.name.lower() for c in o.users_collection)
    force_whole = any(kw.lower() in coll_names for kw in KEEP_WHOLE_KEYWORDS)

    # Measure the object's world-space footprint.
    corners = [o.matrix_world @ Vector(c) for c in o.bound_box]
    obj_w = max(c.x for c in corners) - min(c.x for c in corners)
    obj_h = max(c.y for c in corners) - min(c.y for c in corners)
    too_big = (obj_w >= chunk_w * SLICE_SIZE_RATIO or
               obj_h >= chunk_h * SLICE_SIZE_RATIO)

    if force_whole:
        # Protected keyword wins — always keep this object intact.
        to_group.append(o)
    elif too_big:
        # Object is large enough to cross chunk boundaries; needs cutting.
        to_slice.append(o)
    else:
        # Small object fits in one chunk; just link it as-is.
        to_group.append(o)

log(f"To slice (too big for one chunk): {len(to_slice)}")
log(f"To group whole:                   {len(to_group)}")

# ── Step 3: rebuild the _chunks collection hierarchy ──
# All chunk sub-collections live under a single "_chunks" parent so the
# Outliner stays organised. On re-runs, CLEAR_PREVIOUS removes stale geometry
# (slice parts and gizmo empties) without touching the original source objects.
scene_coll = bpy.context.scene.collection
chunks_root = bpy.data.collections.get("_chunks")
if chunks_root and CLEAR_PREVIOUS:
    for cc in list(chunks_root.children):
        for obj in list(cc.objects):
            cc.objects.unlink(obj)
            # Only delete objects that were generated by this script.
            # Source objects (no "_part_" or "_bounds" in their name) are left alone.
            if not obj.users_collection and (
                    '_part_' in obj.name or '_bounds' in obj.name):
                bpy.data.objects.remove(obj, do_unlink=True)
        bpy.data.collections.remove(cc)

if chunks_root is None:
    chunks_root = bpy.data.collections.new("_chunks")
    scene_coll.children.link(chunks_root)

# Pre-create one sub-collection per grid cell so objects can be linked later.
chunk_colls = {}
for cx in range(GRID_X):
    for cy in range(GRID_Y):
        cc = bpy.data.collections.new(f"chunk_{cx:02d}_{cy:02d}")
        chunks_root.children.link(cc)
        chunk_colls[(cx, cy)] = cc

# ── Step 4: slice pipeline ──
# Each "too big" object is evaluated with modifiers applied, converted to a
# BMesh, bisected along every chunk boundary that its bounding box crosses,
# then split into per-chunk output meshes.
total_parts = 0
for src in to_slice:
    corners = [src.matrix_world @ Vector(c) for c in src.bound_box]
    ox_min = min(c.x for c in corners); ox_max = max(c.x for c in corners)
    oy_min = min(c.y for c in corners); oy_max = max(c.y for c in corners)

    # Find the range of chunks the object overlaps.
    # The small epsilon nudge keeps bbox edges from landing on the wrong side
    # of a boundary plane due to floating-point imprecision.
    cx_min, cy_min = world_to_chunk(ox_min + 0.001, oy_min + 0.001)
    cx_max, cy_max = world_to_chunk(ox_max - 0.001, oy_max - 0.001)

    if cx_min == cx_max and cy_min == cy_max:
        # After the epsilon nudge the object actually fits in one cell;
        # just link it directly without any cutting.
        try:
            chunk_colls[(cx_min, cy_min)].objects.link(src)
        except RuntimeError:
            pass
        total_parts += 1
        continue

    # Evaluate the object (applies modifiers, shape keys, etc.) and bake it
    # into a standalone mesh so the original is not modified.
    depsgraph = bpy.context.evaluated_depsgraph_get()
    obj_eval = src.evaluated_get(depsgraph)
    eval_mesh = bpy.data.meshes.new_from_object(obj_eval)

    bm = bmesh.new()
    bm.from_mesh(eval_mesh)
    bm.transform(src.matrix_world)   # bring geometry into world space
    bpy.data.meshes.remove(eval_mesh)

    if MERGE_DOUBLES:
        # Remove duplicate vertices before cutting to avoid t-junction seams.
        bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=0.0001)

    # Snapshot each face's outward normal BEFORE bisecting. The custom face
    # layer is propagated by bisect_plane to the child faces it produces, so
    # after the cuts every fragment still carries the direction its parent
    # face had in the source mesh. We use this snapshot at the end of the
    # pipeline to re-flip any fragment whose winding ended up reversed.
    bm.normal_update()
    ref_normal = bm.faces.layers.float_vector.new("__ref_normal")
    for f in bm.faces:
        f[ref_normal] = f.normal.copy()

    # Bisect along each vertical (X-aligned) boundary plane between columns.
    # bisect_plane with clear_inner/clear_outer=False keeps both halves;
    # faces will be separated into buckets by their centre point afterwards.
    for k in range(cx_min + 1, cx_max + 1):
        plane_x = all_min.x + k * chunk_w
        bmesh.ops.bisect_plane(
            bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:],
            dist=0.0001,
            plane_co=(plane_x, 0, 0), plane_no=(1, 0, 0),
            clear_inner=False, clear_outer=False,
        )
    # Bisect along each horizontal (Y-aligned) boundary plane between rows.
    for k in range(cy_min + 1, cy_max + 1):
        plane_y = all_min.y + k * chunk_h
        bmesh.ops.bisect_plane(
            bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:],
            dist=0.0001,
            plane_co=(0, plane_y, 0), plane_no=(0, 1, 0),
            clear_inner=False, clear_outer=False,
        )

    # Do NOT use bmesh.ops.recalc_face_normals here: it assumes a closed
    # volume and on open ribbons (roads, fences, terrain strips) it flips
    # arbitrary subsets of faces, which is exactly the bug we want to
    # avoid. Instead, restore each face to the direction we snapshotted
    # before the bisect cuts. If the current normal points opposite to
    # the stored reference, flip the face's winding.
    bm.normal_update()
    for f in bm.faces:
        if f.normal.dot(f[ref_normal]) < 0.0:
            f.normal_flip()

    bm.faces.ensure_lookup_table()
    bm.verts.ensure_lookup_table()

    # Assign each face to the chunk that contains its median centre point.
    buckets = defaultdict(list)
    for f in bm.faces:
        c = f.calc_center_median()
        buckets[world_to_chunk(c.x, c.y)].append(f)

    # Preserve UV and vertex-colour layer names so materials work after import.
    uv_names = list(bm.loops.layers.uv.keys())
    col_names = list(bm.loops.layers.color.keys())

    # Build a separate BMesh and mesh object for each chunk that received faces.
    for (cx, cy), src_faces in buckets.items():
        bm_out = bmesh.new()

        # Create matching UV and colour layers in the output BMesh.
        uv_pairs = [(bm.loops.layers.uv[n], bm_out.loops.layers.uv.new(n))
                    for n in uv_names]
        col_pairs = [(bm.loops.layers.color[n], bm_out.loops.layers.color.new(n))
                     for n in col_names]

        pivot = chunk_pivot(cx, cy)

        # Copy vertices, translating them so the chunk pivot becomes the local origin.
        # This pre-centres the geometry for sliced parts (the object's location will
        # be set to pivot separately).
        vert_map = {}
        for sf in src_faces:
            for v in sf.verts:
                if v not in vert_map:
                    nv = bm_out.verts.new(v.co - pivot)
                    nv.normal = v.normal
                    vert_map[v] = nv
        bm_out.verts.ensure_lookup_table()

        # Rebuild faces from the remapped vertices, copying material index,
        # smooth flag, UVs, and vertex colours.
        skipped_faces = 0
        for sf in src_faces:
            try:
                nf = bm_out.faces.new([vert_map[v] for v in sf.verts])
            except ValueError:
                # bisect_plane can produce zero-area faces on the cut plane
                # (e.g. a face reduced to a line). These are genuinely invalid
                # and cannot be added to a mesh, so we skip them.
                # Any skip is counted and reported so unexpected losses are visible.
                skipped_faces += 1
                continue
            nf.material_index = sf.material_index
            nf.smooth = sf.smooth
            for sl, nl in zip(sf.loops, nf.loops):
                for s_l, n_l in uv_pairs:
                    nl[n_l].uv = sl[s_l].uv
                for s_l, n_l in col_pairs:
                    nl[n_l] = sl[s_l]

        if skipped_faces:
            log(f"  [WARNING] {src.name} → chunk {cx:02d}_{cy:02d}: "
                f"skipped {skipped_faces}/{len(src_faces)} faces (degenerate). "
                f"Check the mesh for zero-area geometry near chunk boundaries.")

        bm_out.normal_update()

        part_name = f"{src.name}_part_{cx:02d}_{cy:02d}"
        me = bpy.data.meshes.new(part_name)
        bm_out.to_mesh(me)
        bm_out.free()

        # Copy material slots from the source object so the part renders correctly.
        for mat in src.data.materials:
            me.materials.append(mat)

        # Place the part object at the chunk pivot; its mesh is already centred there.
        part_obj = bpy.data.objects.new(part_name, me)
        part_obj.location = pivot
        chunk_colls[(cx, cy)].objects.link(part_obj)
        total_parts += 1

    bm.free()

log(f"\nSlice path: {len(to_slice)} sources → {total_parts} chunk entries")

# ── Step 5: group (whole-object) pipeline ──
# Objects that fit in one cell, or are protected by KEEP_WHOLE_KEYWORDS,
# are simply linked into the collection of whichever chunk their centre falls in.
for b in to_group:
    corners = [b.matrix_world @ Vector(c) for c in b.bound_box]
    center_x = sum(c.x for c in corners) / 8.0
    center_y = sum(c.y for c in corners) / 8.0
    key = world_to_chunk(center_x, center_y)
    try:
        chunk_colls[key].objects.link(b)
    except RuntimeError:
        # Object is already linked to this collection (can happen with multi-user data).
        pass

log(f"Group path: {len(to_group)} objects linked into chunks")

# ── Step 6: remove empty chunk collections ──
# Chunks with no geometry are deleted to keep the Outliner clean and to avoid
# exporting empty FBX files.
empty = 0
for key in list(chunk_colls.keys()):
    if len(chunk_colls[key].objects) == 0:
        bpy.data.collections.remove(chunk_colls[key])
        del chunk_colls[key]
        empty += 1
log(f"Removed {empty} empty chunks, {len(chunk_colls)} non-empty remain")

# ── Step 7: FBX export with temporary centring ──
# Each chunk's mesh objects are temporarily shifted by -pivot so the FBX
# origin lands at (0, 0, 0). Original locations are restored unconditionally
# in a finally block so the scene is never left in a shifted state even if
# the exporter raises an exception.
#
# FBX export settings are chosen to match the Unity standard preset:
#   use_space_transform=True + bake_space_transform=False stores mesh data in
#   Blender's Z-up coordinate system and adds a compensating rotation on the
#   FBX root node. Unity reads that root rotation to flip the asset to Y-up.
#   The Unity ChunkImport script preserves this root rotation and only resets
#   the child's localPosition — do NOT change these two flags without updating
#   the Unity importer as well.
if DO_EXPORT:
    log(f"\nExporting FBX to {OUTPUT_DIR}")

    orig_active = bpy.context.view_layer.objects.active
    orig_sel = list(bpy.context.selected_objects)
    exported = 0

    for (cx, cy), cc in chunk_colls.items():
        mesh_objs = [o for o in cc.objects if o.type == 'MESH']
        if not mesh_objs:
            continue

        pivot = chunk_pivot(cx, cy)

        # Save world-space locations before the temporary shift.
        saved_locations = [(o, o.location.copy()) for o in mesh_objs]

        try:
            # Move every object in the chunk so the chunk pivot is at origin.
            for o in mesh_objs:
                o.location = o.location - pivot
            bpy.context.view_layer.update()

            # Select only the objects belonging to this chunk for export.
            bpy.ops.object.select_all(action='DESELECT')
            for o in mesh_objs:
                o.select_set(True)
            bpy.context.view_layer.objects.active = mesh_objs[0]

            # Output filename: XX_YY.fbx — XX = column (X), YY = row (Y/Z).
            # The Unity ChunkImport expects exactly this naming convention.
            filepath = os.path.join(OUTPUT_DIR, f"{cx:02d}_{cy:02d}.fbx")
            bpy.ops.export_scene.fbx(
                filepath=filepath,
                check_existing=False,
                use_selection=True,
                global_scale=1.0,
                apply_unit_scale=True,
                apply_scale_options='FBX_SCALE_NONE',
                # These two settings together produce Unity's standard import behaviour.
                # See the note above Step 7 before changing them.
                use_space_transform=True,
                bake_space_transform=False,
                axis_forward='-Z', axis_up='Y',
                object_types={'MESH'},
                use_mesh_modifiers=True,
                mesh_smooth_type='FACE',
                use_tspace=False,
                use_triangles=False,
                path_mode='COPY',
                embed_textures=False,
                add_leaf_bones=False,
                bake_anim=False,
            )
            exported += 1
            log(f"  {cx:02d}_{cy:02d}.fbx "
                f"({len(mesh_objs)} obj, pivot at "
                f"{pivot.x:.1f},{pivot.y:.1f})")
        finally:
            # Always restore original locations, even if the exporter failed.
            for o, loc in saved_locations:
                o.location = loc
            bpy.context.view_layer.update()

    # Restore the original selection and active object.
    bpy.ops.object.select_all(action='DESELECT')
    for o in orig_sel:
        try: o.select_set(True)
        except: pass
    if orig_active:
        try: bpy.context.view_layer.objects.active = orig_active
        except: pass

    log(f"Exported {exported} FBX files")

log("Done.")

_log_file.close()
