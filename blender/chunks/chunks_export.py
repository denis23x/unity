import bpy
import bmesh
import math
import os
from mathutils import Matrix, Vector
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

# Merge vertices closer than 0.0001 m before slicing to avoid t-junctions
# and seam artifacts along chunk boundaries.
MERGE_DOUBLES = True

# Set to False to skip FBX export and only build the _chunks collections
# inside the temp scene (useful when debugging the slice/group logic).
DO_EXPORT = True

# Name of the throw-away scene the script builds in. Anything inside it is
# deleted at the end; nothing in the user's source scene is ever mutated.
TEMP_SCENE_NAME = "__chunks_temp__"

# Logging
# OUTPUT_DIR is created here unconditionally so the log file is always written,
# even when DO_EXPORT is False (dry-run / visual-check mode).
os.makedirs(OUTPUT_DIR, exist_ok=True)
_log_path = os.path.join(OUTPUT_DIR, "chunks_export.log")
_log_file = open(_log_path, "w", encoding="utf-8")

def log(msg=""):
    _log_file.write(msg + "\n")
    _log_file.flush()


def apply_unity_fix(ob):
    """Replicate Edy Garcia's Unity-FBX rotation trick on a single object.

    Bakes a -90°X rotation into the mesh data and leaves a compensating
    +90°X on the object's local transform. When the stock FBX exporter
    runs afterwards with its default Unity-ish axes (use_space_transform=True,
    axis_up='Y'), the FBX root lands in Unity with identity TRS — no
    scale=100, no -90°X spin, no Unity-side bake.

    Why we re-implement it instead of calling bpy.ops.export_scene.unity_fbx:
    Edy's operator wraps every export in bpy.ops.ed.undo_push + bpy.ops.ed.undo,
    whose memfile snapshot covers all of bpy.data — on a heavy baked scene
    that dominates per-chunk cost and scales linearly with the chunk count.
    The temp scene gets deleted in the outer finally anyway, so the per-call
    undo bracket is pure waste for our workflow.

    Source: https://github.com/EdyJ/blender-to-unity-fbx-exporter (MIT).
    """
    # Single-user mesh data is required for transform_apply.
    if ob.data is not None and ob.data.users > 1:
        ob.data = ob.data.copy()

    # Reset parent inverse so editing matrix_local has the expected effect.
    if ob.parent:
        mat_world = ob.matrix_world.copy()
        ob.matrix_parent_inverse.identity()
        ob.matrix_basis = ob.parent.matrix_world.inverted() @ mat_world

    mat_original = ob.matrix_local.copy()
    ob.matrix_local = Matrix.Rotation(math.radians(-90.0), 4, 'X')

    # transform_apply operates on the active selection.
    bpy.ops.object.select_all(action='DESELECT')
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=False)

    ob.matrix_local = mat_original @ Matrix.Rotation(math.radians(90.0), 4, 'X')


def remove_scene_with_data(scene_name):
    """Remove a scene along with the objects and meshes that lived only in it.

    Safe to call even if the scene does not exist. Used both as the final
    teardown and as a safety net at startup to wipe a leftover temp scene
    from a previously aborted run.
    """
    scene = bpy.data.scenes.get(scene_name)
    if scene is None:
        return
    objs = list(scene.collection.all_objects)
    meshes = {o.data for o in objs if o.type == 'MESH' and o.data is not None}
    sub_colls = list(scene.collection.children_recursive)
    for o in objs:
        try:
            bpy.data.objects.remove(o, do_unlink=True)
        except (RuntimeError, ReferenceError):
            pass
    for m in meshes:
        try:
            if m.users == 0:
                bpy.data.meshes.remove(m, do_unlink=True)
        except (RuntimeError, ReferenceError):
            pass
    for c in sub_colls:
        try:
            bpy.data.collections.remove(c)
        except (RuntimeError, ReferenceError):
            pass
    try:
        bpy.data.scenes.remove(scene, do_unlink=True)
    except (RuntimeError, ReferenceError):
        pass


# Collect all selected mesh objects from the user's current scene. The temp
# scene we create below will preserve this selection on its copies, but we
# also record names as a fallback diagnostic.
selected_names = [o.name for o in bpy.context.selected_objects if o.type == 'MESH']
if not selected_names:
    _log_file.close()
    raise Exception("Select mesh objects first (press A in the viewport)")

orig_scene_name = bpy.context.scene.name
log(f"Source scene: {orig_scene_name}")
log(f"Selected mesh objects: {len(selected_names)}")
log(f"Grid: {GRID_X} × {GRID_Y}")

# Clean up any stale temp scene left behind by a previous aborted run so the
# FULL_COPY below cannot collide with it on naming.
remove_scene_with_data(TEMP_SCENE_NAME)


# ── Step 1: create a temporary scene as a full copy of the current one ────────
# FULL_COPY duplicates every object, mesh, material and collection and switches
# the active context to the new scene. Selection state on the copies mirrors
# the originals, so the rest of the script can work off bpy.context.selected_objects
# without ever touching the user's source data.
bpy.ops.scene.new(type='FULL_COPY')
temp_scene = bpy.context.scene
temp_scene.name = TEMP_SCENE_NAME
log(f"Temp scene created: {temp_scene.name}")


try:
    # ── Step 2: bake every selected mesh in the temp scene ───────────────────
    # Apply all modifiers and constraints once, while objects still sit at
    # their original world positions. After this step the scene contains only
    # static geometry, so any later pivot shift is a pure transform move and
    # cannot trigger Array/Curve/Shrinkwrap modifiers or Shrinkwrap constraints
    # to re-fire from a new origin.
    #
    # We snapshot every object's evaluated state with one depsgraph read
    # BEFORE mutating anything, then apply the bakes in a second pass. That
    # ordering guarantees inter-object dependencies (e.g. one object's
    # Shrinkwrap targets another in the same selection) see consistent input.
    selected = [o for o in bpy.context.selected_objects if o.type == 'MESH']
    log(f"Baking {len(selected)} objects in temp scene")

    depsgraph = bpy.context.evaluated_depsgraph_get()
    bake_plan = []
    for o in selected:
        plan = {'obj': o, 'baked_mesh': None, 'baked_matrix': None}
        if o.modifiers:
            obj_eval = o.evaluated_get(depsgraph)
            plan['baked_mesh'] = bpy.data.meshes.new_from_object(obj_eval)
        if o.constraints:
            # matrix_world here is the constraint-evaluated transform.
            # Writing it back after clearing constraints preserves the
            # visual position without leaving anything that could re-fire.
            plan['baked_matrix'] = o.matrix_world.copy()
        bake_plan.append(plan)

    with_modifiers = 0
    with_constraints = 0
    for plan in bake_plan:
        o = plan['obj']
        if plan['baked_mesh'] is not None:
            old_mesh = o.data
            o.data = plan['baked_mesh']
            o.modifiers.clear()
            if old_mesh.users == 0:
                bpy.data.meshes.remove(old_mesh)
            with_modifiers += 1
        if plan['baked_matrix'] is not None:
            o.constraints.clear()
            o.matrix_world = plan['baked_matrix']
            with_constraints += 1
    bpy.context.view_layer.update()
    log(f"  baked modifiers on {with_modifiers}, baked constraints on {with_constraints}")

    # ── Step 3: compute the world-space bounding box on the baked meshes ─────
    # Because every modifier has been applied, the bound_box of each object
    # already reflects its final geometry — no surprises from displacements
    # or shrinkwrap pulls that would shift bboxes after a naive read.
    all_min = Vector((float('inf'),) * 3)
    all_max = Vector((float('-inf'),) * 3)
    for o in selected:
        for c in o.bound_box:
            wc = o.matrix_world @ Vector(c)
            all_min = Vector((min(all_min[i], wc[i]) for i in range(3)))
            all_max = Vector((max(all_max[i], wc[i]) for i in range(3)))

    span_x = all_max.x - all_min.x
    span_y = all_max.y - all_min.y
    if span_x == 0 or span_y == 0:
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

        Clamps the result so points exactly on the far edge land in the last
        cell rather than an out-of-range index.
        """
        cx = int((x - all_min.x) / chunk_w)
        cy = int((y - all_min.y) / chunk_h)
        return (max(0, min(GRID_X - 1, cx)),
                max(0, min(GRID_Y - 1, cy)))


    def chunk_pivot(cx, cy):
        """Return the world-space center of chunk (cx, cy) at Z = 0.

        This is the point used as the FBX export origin (each chunk's objects
        are shifted by -pivot before export), and the value Unity stores in
        the scene root's world position.
        """
        return Vector((all_min.x + (cx + 0.5) * chunk_w,
                       all_min.y + (cy + 0.5) * chunk_h,
                       0.0))


    # ── Step 4: categorise objects into "slice" or "group whole" ─────────────
    # An object is routed to the slice pipeline only when it is too large to
    # fit in a single chunk AND it does not belong to a protected collection.
    to_slice, to_group = [], []
    for o in selected:
        coll_names = ' '.join(c.name.lower() for c in o.users_collection)
        force_whole = any(kw.lower() in coll_names for kw in KEEP_WHOLE_KEYWORDS)

        corners = [o.matrix_world @ Vector(c) for c in o.bound_box]
        obj_w = max(c.x for c in corners) - min(c.x for c in corners)
        obj_h = max(c.y for c in corners) - min(c.y for c in corners)
        too_big = (obj_w >= chunk_w * SLICE_SIZE_RATIO or
                   obj_h >= chunk_h * SLICE_SIZE_RATIO)

        if force_whole:
            to_group.append(o)
        elif too_big:
            to_slice.append(o)
        else:
            to_group.append(o)

    log(f"To slice (too big for one chunk): {len(to_slice)}")
    log(f"To group whole:                   {len(to_group)}")

    # ── Step 5: build the _chunks collection hierarchy inside the temp scene ──
    chunks_root = bpy.data.collections.new("_chunks")
    temp_scene.collection.children.link(chunks_root)
    chunk_colls = {}
    for cx in range(GRID_X):
        for cy in range(GRID_Y):
            cc = bpy.data.collections.new(f"chunk_{cx:02d}_{cy:02d}")
            chunks_root.children.link(cc)
            chunk_colls[(cx, cy)] = cc

    # ── Step 6: slice pipeline ───────────────────────────────────────────────
    # Each "too big" object is read directly (modifiers are already baked in),
    # converted to a BMesh, bisected along every chunk boundary that its
    # bounding box crosses, then split into per-chunk output meshes.
    total_parts = 0
    for src in to_slice:
        corners = [src.matrix_world @ Vector(c) for c in src.bound_box]
        ox_min = min(c.x for c in corners); ox_max = max(c.x for c in corners)
        oy_min = min(c.y for c in corners); oy_max = max(c.y for c in corners)

        # Find the range of chunks the object overlaps. The small epsilon
        # nudge keeps bbox edges from landing on the wrong side of a boundary
        # plane due to floating-point imprecision.
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

        bm = bmesh.new()
        bm.from_mesh(src.data)
        bm.transform(src.matrix_world)   # bring geometry into world space

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

    log(f"Slice path: {len(to_slice)} sources → {total_parts} chunk entries")

    # ── Step 7: group (whole-object) pipeline ────────────────────────────────
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
            # Object is already linked to this collection (rare but possible).
            pass

    log(f"Group path: {len(to_group)} objects linked into chunks")

    # ── Step 8: drop empty chunk collections ─────────────────────────────────
    # Chunks with no geometry are deleted to keep the Outliner clean and to
    # avoid exporting empty FBX files.
    empty = 0
    for key in list(chunk_colls.keys()):
        if len(chunk_colls[key].objects) == 0:
            bpy.data.collections.remove(chunk_colls[key])
            del chunk_colls[key]
            empty += 1
    log(f"Removed {empty} empty chunks, {len(chunk_colls)} non-empty remain")

    # ── Step 9: FBX export with the Unity-identity rotation trick inlined ────
    # We apply Edy Garcia's -90°X-into-mesh / +90°X-on-transform trick to
    # each chunk object ourselves and then call the stock FBX exporter
    # directly. The trick is the same as bpy.ops.export_scene.unity_fbx, but
    # without that operator's per-call bpy.ops.ed.undo_push + bpy.ops.ed.undo
    # bracket — memfile undo snapshots all of bpy.data, which on a heavy
    # baked scene dominates per-chunk cost and scales linearly with the chunk
    # count. The temp scene is deleted in the outer finally, so we never need
    # to "restore" anything after each export.
    #
    # Stock exporter args are taken straight from Edy's internal call:
    # apply_scale_options='FBX_SCALE_UNITS', use_custom_props=True, default
    # axes (axis_forward='-Z', axis_up='Y' implicitly via use_space_transform).
    # The Unity ChunkImporter depends on this contract; do not change axes
    # or scale flags here without updating the Unity side first.
    if DO_EXPORT:
        log(f"\nExporting FBX to {OUTPUT_DIR}")
        exported = 0

        # Pre-compute per-chunk mesh object names BEFORE the export loop so
        # we never need to read cc.objects across an exporter call.
        chunk_exports = []
        for (cx, cy), cc in chunk_colls.items():
            mesh_obj_names = [o.name for o in cc.objects if o.type == 'MESH']
            if mesh_obj_names:
                chunk_exports.append(((cx, cy), mesh_obj_names))

        for (cx, cy), mesh_obj_names in chunk_exports:
            pivot = chunk_pivot(cx, cy)

            # Shift each object so the chunk pivot lands at the world origin.
            # No restore afterwards — the temp scene will be deleted, and
            # each chunk owns a disjoint set of objects so there is nothing
            # to preserve for later iterations.
            for name in mesh_obj_names:
                obj = bpy.data.objects.get(name)
                if obj is not None:
                    obj.location = obj.location - pivot
            bpy.context.view_layer.update()

            # Apply the Unity-identity rotation trick on each chunk object.
            for name in mesh_obj_names:
                obj = bpy.data.objects.get(name)
                if obj is not None:
                    apply_unity_fix(obj)

            # Final selection for the export.
            bpy.ops.object.select_all(action='DESELECT')
            first = None
            for name in mesh_obj_names:
                obj = bpy.data.objects.get(name)
                if obj is None:
                    continue
                obj.select_set(True)
                if first is None:
                    first = obj
            if first is not None:
                bpy.context.view_layer.objects.active = first

            # NN_MM.fbx — NN = column (X), MM = row (Y/Z). The Unity
            # ChunkImporter expects exactly this naming convention.
            filepath = os.path.join(OUTPUT_DIR, f"{cx:02d}_{cy:02d}.fbx")
            bpy.ops.export_scene.fbx(
                filepath=filepath,
                apply_scale_options='FBX_SCALE_UNITS',
                object_types={'MESH'},
                use_custom_props=True,
                use_active_collection=False,
                use_selection=True,
                add_leaf_bones=False,
                primary_bone_axis='Y',
                secondary_bone_axis='X',
                use_tspace=False,
                use_triangles=False,
            )
            exported += 1
            log(f"  {cx:02d}_{cy:02d}.fbx ({len(mesh_obj_names)} obj, "
                f"pivot {pivot.x:.1f},{pivot.y:.1f})")

        log(f"Exported {exported} FBX files")

finally:
    # ── Step 10: tear down the temp scene ────────────────────────────────────
    # Switch the window back to the original scene first so removing the temp
    # one does not leave Blender without an active scene. Scene/window refs
    # are re-resolved by name in case Edy's internal undo invalidated them.
    orig_scene = bpy.data.scenes.get(orig_scene_name)
    if orig_scene is not None:
        try:
            bpy.context.window.scene = orig_scene
        except (AttributeError, ReferenceError):
            pass
    remove_scene_with_data(TEMP_SCENE_NAME)
    log("Temp scene removed.")

log("Done.")
_log_file.close()
