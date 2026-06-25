import bpy
from mathutils import Vector

# Save cursor position to restore at the end
original_cursor = bpy.context.scene.cursor.location.copy()

valid_types = {'MESH', 'CURVE', 'SURFACE', 'META', 'FONT'}
selected = [obj for obj in bpy.context.selected_objects if obj.type in valid_types]

# Deselect everything; we'll process objects one by one
bpy.ops.object.select_all(action='DESELECT')

for obj in selected:
    # Bounding box corners in world coordinates
    corners = [obj.matrix_world @ Vector(c) for c in obj.bound_box]

    min_x = min(c.x for c in corners)
    max_x = max(c.x for c in corners)
    min_y = min(c.y for c in corners)
    max_y = max(c.y for c in corners)
    min_z = min(c.z for c in corners)

    # Bottom center of the bbox
    bpy.context.scene.cursor.location = Vector((
        (min_x + max_x) / 2,
        (min_y + max_y) / 2,
        min_z,
    ))

    # Select only the current object and make it active
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj

    bpy.ops.object.origin_set(type='ORIGIN_CURSOR', center='MEDIAN')

    obj.select_set(False)

# Restore the original selection and cursor
for obj in selected:
    obj.select_set(True)
if selected:
    bpy.context.view_layer.objects.active = selected[0]

bpy.context.scene.cursor.location = original_cursor

print(f"Processed objects: {len(selected)}")