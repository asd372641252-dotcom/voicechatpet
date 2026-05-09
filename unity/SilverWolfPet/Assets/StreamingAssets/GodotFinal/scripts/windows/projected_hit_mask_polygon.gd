extends Polygon2D

@export var enabled := true
@export var camera_path := NodePath("../PetRoot/Camera3D")
@export var model_loader_path := NodePath("../PetRoot/ModelSlot")
@export_range(1.0, 30.0, 1.0) var update_hz := 12.0
@export_range(0.0, 64.0, 1.0) var padding_pixels := 16.0
@export_range(0.0, 32.0, 1.0) var bottom_padding_pixels := 6.0
@export_range(0.0, 24.0, 1.0) var min_update_delta_pixels := 2.0
@export var prefer_skeleton_hit_mask := true
@export var tight_body_hit_mask := true
@export var capsule_hit_test_enabled := true
@export var mesh_fallback_enabled := false
@export var initial_fallback_enabled := false
@export_range(4.0, 80.0, 1.0) var skeleton_point_padding_pixels := 24.0
@export_range(8.0, 96.0, 1.0) var skeleton_head_padding_pixels := 34.0
@export_range(4.0, 64.0, 1.0) var skeleton_limb_padding_pixels := 18.0
@export_range(8.0, 80.0, 1.0) var capsule_body_radius_pixels := 30.0
@export_range(8.0, 80.0, 1.0) var capsule_head_radius_pixels := 34.0
@export_range(4.0, 64.0, 1.0) var capsule_limb_radius_pixels := 16.0
@export_range(4.0, 64.0, 1.0) var capsule_hand_foot_radius_pixels := 13.0

const BODY_BONE_NAMES := [
	"Hips",
	"Spine",
	"Chest",
	"UpperChest",
	"Neck",
	"Head",
	"LeftShoulder",
	"LeftUpperArm",
	"LeftLowerArm",
	"LeftHand",
	"RightShoulder",
	"RightUpperArm",
	"RightLowerArm",
	"RightHand",
	"LeftUpperLeg",
	"LeftLowerLeg",
	"LeftFoot",
	"LeftToes",
	"RightUpperLeg",
	"RightLowerLeg",
	"RightFoot",
	"RightToes",
]

const PMX_BODY_BONE_NAMES := [
	"腰",
	"下半身",
	"上半身",
	"上半身1",
	"上半身2",
	"首",
	"頭",
	"肩.R",
	"腕.R",
	"ひじ.R",
	"手首.R",
	"肩.L",
	"腕.L",
	"ひじ.L",
	"手首.L",
	"足.R",
	"ひざ.R",
	"足首.R",
	"つま先.R",
	"足.L",
	"ひざ.L",
	"足首.L",
	"つま先.L",
]

const TIGHT_BODY_BONE_NAMES := [
	"Hips",
	"Spine",
	"Chest",
	"UpperChest",
	"Neck",
	"Head",
	"LeftShoulder",
	"RightShoulder",
	"LeftUpperLeg",
	"RightUpperLeg",
	"LeftLowerLeg",
	"RightLowerLeg",
	"LeftFoot",
	"RightFoot",
]

const PMX_TIGHT_BODY_BONE_NAMES := [
	"腰",
	"下半身",
	"上半身",
	"上半身1",
	"上半身2",
	"首",
	"頭",
	"肩.R",
	"肩.L",
	"足.R",
	"ひざ.R",
	"足首.R",
	"足.L",
	"ひざ.L",
	"足首.L",
]

const CAPSULE_BONE_NAMES := [
	"Hips",
	"Spine",
	"Chest",
	"UpperChest",
	"Neck",
	"Head",
	"LeftShoulder",
	"LeftUpperArm",
	"LeftLowerArm",
	"LeftHand",
	"RightShoulder",
	"RightUpperArm",
	"RightLowerArm",
	"RightHand",
	"LeftUpperLeg",
	"LeftLowerLeg",
	"LeftFoot",
	"RightUpperLeg",
	"RightLowerLeg",
	"RightFoot",
]

const PMX_CAPSULE_BONE_NAMES := [
	"腰",
	"下半身",
	"上半身",
	"上半身1",
	"上半身2",
	"首",
	"頭",
	"肩.R",
	"腕.R",
	"ひじ.R",
	"手首.R",
	"肩.L",
	"腕.L",
	"ひじ.L",
	"手首.L",
	"足.R",
	"ひざ.R",
	"足首.R",
	"足.L",
	"ひざ.L",
	"足首.L",
]

const CAPSULE_SEGMENTS := [
	["Hips", "Spine"],
	["Spine", "Chest"],
	["Chest", "UpperChest"],
	["UpperChest", "Neck"],
	["Neck", "Head"],
	["Chest", "LeftShoulder"],
	["LeftShoulder", "LeftUpperArm"],
	["LeftUpperArm", "LeftLowerArm"],
	["LeftLowerArm", "LeftHand"],
	["Chest", "RightShoulder"],
	["RightShoulder", "RightUpperArm"],
	["RightUpperArm", "RightLowerArm"],
	["RightLowerArm", "RightHand"],
	["Hips", "LeftUpperLeg"],
	["LeftUpperLeg", "LeftLowerLeg"],
	["LeftLowerLeg", "LeftFoot"],
	["Hips", "RightUpperLeg"],
	["RightUpperLeg", "RightLowerLeg"],
	["RightLowerLeg", "RightFoot"],
]

const PMX_CAPSULE_SEGMENTS := [
	["腰", "下半身"],
	["腰", "上半身"],
	["上半身", "上半身1"],
	["上半身1", "上半身2"],
	["上半身2", "首"],
	["首", "頭"],
	["上半身2", "肩.R"],
	["肩.R", "腕.R"],
	["腕.R", "ひじ.R"],
	["ひじ.R", "手首.R"],
	["上半身2", "肩.L"],
	["肩.L", "腕.L"],
	["腕.L", "ひじ.L"],
	["ひじ.L", "手首.L"],
	["下半身", "足.R"],
	["足.R", "ひざ.R"],
	["ひざ.R", "足首.R"],
	["下半身", "足.L"],
	["足.L", "ひざ.L"],
	["ひざ.L", "足首.L"],
]

const HEAD_BONES := {
	"Head": true,
	"Neck": true,
	"頭": true,
	"首": true,
}

const LIMB_END_BONES := {
	"LeftHand": true,
	"RightHand": true,
	"LeftFoot": true,
	"RightFoot": true,
	"LeftToes": true,
	"RightToes": true,
	"手首.R": true,
	"手首.L": true,
	"足首.R": true,
	"足首.L": true,
	"つま先.R": true,
	"つま先.L": true,
}

var _camera: Camera3D
var _model_loader: Node
var _model_root: Node
var _fallback_polygon := PackedVector2Array()
var _last_polygon := PackedVector2Array()
var _projected_bone_points: Dictionary = {}
var _accumulator := 0.0


func _ready() -> void:
	if initial_fallback_enabled:
		_fallback_polygon = polygon
	else:
		_fallback_polygon = PackedVector2Array()
		polygon = PackedVector2Array()
	call_deferred("_bind_nodes")


func _process(delta: float) -> void:
	if not enabled:
		return
	_accumulator += delta
	var interval := 1.0 / maxf(update_hz, 1.0)
	if _accumulator < interval:
		return
	_accumulator = 0.0
	_rebuild_polygon()


func force_rebuild() -> void:
	_rebuild_polygon(true)


func has_active_hit_mask() -> bool:
	return capsule_hit_test_enabled and _projected_bone_points.size() >= 3


func debug_get_projected_bone_count() -> int:
	return _projected_bone_points.size()


func is_point_inside_hit_mask(window_local_position: Vector2) -> bool:
	if capsule_hit_test_enabled and not _projected_bone_points.is_empty():
		return _is_point_inside_capsule_mask(window_local_position)
	if polygon.size() < 3:
		return false
	var polygon_local_position := to_local(window_local_position)
	return Geometry2D.is_point_in_polygon(polygon_local_position, polygon)


func _bind_nodes() -> void:
	_camera = get_node_or_null(camera_path) as Camera3D
	_model_loader = get_node_or_null(model_loader_path)
	if _model_loader != null:
		if _model_loader.has_signal("model_loaded"):
			var callback := Callable(self, "_on_model_loaded")
			if not _model_loader.is_connected("model_loaded", callback):
				_model_loader.connect("model_loaded", callback)
		if _model_loader.has_method("get_model_root"):
			_model_root = _model_loader.call("get_model_root")
	_rebuild_polygon(true)


func _on_model_loaded(model_root: Node) -> void:
	_model_root = model_root
	call_deferred("force_rebuild")


func _rebuild_polygon(force := false) -> void:
	if _camera == null or not is_instance_valid(_camera):
		_camera = get_node_or_null(camera_path) as Camera3D
	if _model_root == null or not is_instance_valid(_model_root):
		if _model_loader != null and _model_loader.has_method("get_model_root"):
			_model_root = _model_loader.call("get_model_root")
	if _camera == null or _model_root == null:
		return

	_refresh_projected_bone_points(_model_root)

	var projected_points: Array[Vector2] = []
	if prefer_skeleton_hit_mask:
		if tight_body_hit_mask:
			_collect_projected_tight_body_points(_model_root, projected_points)
		else:
			_collect_projected_skeleton_points(_model_root, projected_points)
	if projected_points.size() < 3 and mesh_fallback_enabled:
		_collect_projected_mesh_points(_model_root, projected_points)
	if projected_points.size() < 3:
		if force and not _fallback_polygon.is_empty():
			polygon = _fallback_polygon
		return

	var hull := _convex_hull(projected_points)
	if hull.size() < 3:
		return
	hull = _expand_polygon(hull, padding_pixels, bottom_padding_pixels)
	if not force and _polygons_close(_last_polygon, hull):
		return
	_last_polygon = hull
	polygon = hull
	queue_redraw()


func _refresh_projected_bone_points(node: Node) -> void:
	_projected_bone_points.clear()
	var skeleton := _find_best_body_skeleton(node)
	if skeleton == null:
		return

	for bone_name in CAPSULE_BONE_NAMES + PMX_CAPSULE_BONE_NAMES:
		var bone_idx := skeleton.find_bone(bone_name)
		if bone_idx < 0:
			continue
		var bone_transform := skeleton.global_transform * skeleton.get_bone_global_pose(bone_idx)
		var world_point := bone_transform.origin
		if _camera.is_position_behind(world_point):
			continue
		_projected_bone_points[bone_name] = _camera.unproject_position(world_point)


func _collect_projected_tight_body_points(node: Node, output: Array[Vector2]) -> void:
	var skeleton := _find_best_body_skeleton(node)
	if skeleton == null:
		return

	for bone_name in TIGHT_BODY_BONE_NAMES + PMX_TIGHT_BODY_BONE_NAMES:
		var bone_idx := skeleton.find_bone(bone_name)
		if bone_idx < 0:
			continue
		var bone_transform := skeleton.global_transform * skeleton.get_bone_global_pose(bone_idx)
		var world_point := bone_transform.origin
		if _camera.is_position_behind(world_point):
			continue

		var screen_point := _camera.unproject_position(world_point)
		_append_padded_screen_point(output, screen_point, _tight_padding_for_bone(bone_name))


func _collect_projected_skeleton_points(node: Node, output: Array[Vector2]) -> void:
	var skeleton := _find_best_body_skeleton(node)
	if skeleton == null:
		return

	for bone_name in BODY_BONE_NAMES + PMX_BODY_BONE_NAMES:
		var bone_idx := skeleton.find_bone(bone_name)
		if bone_idx < 0:
			continue
		var bone_transform := skeleton.global_transform * skeleton.get_bone_global_pose(bone_idx)
		var world_point := bone_transform.origin
		if _camera.is_position_behind(world_point):
			continue

		var screen_point := _camera.unproject_position(world_point)
		_append_padded_screen_point(output, screen_point, _padding_for_bone(bone_name))


func _find_best_body_skeleton(node: Node) -> Skeleton3D:
	var skeletons: Array[Skeleton3D] = []
	_collect_skeletons(node, skeletons)
	var best_skeleton: Skeleton3D = null
	var best_score := -1
	for skeleton in skeletons:
		var score := 0
		for bone_name in BODY_BONE_NAMES + PMX_BODY_BONE_NAMES:
			if skeleton.find_bone(bone_name) >= 0:
				score += 1
		if score > best_score:
			best_score = score
			best_skeleton = skeleton
	return best_skeleton if best_score >= 8 else null


func _collect_skeletons(node: Node, output: Array[Skeleton3D]) -> void:
	if node is Skeleton3D:
		output.append(node as Skeleton3D)
	for child in node.get_children():
		_collect_skeletons(child, output)


func _append_padded_screen_point(output: Array[Vector2], point: Vector2, radius: float) -> void:
	output.append(point)
	output.append(point + Vector2(radius, 0.0))
	output.append(point + Vector2(-radius, 0.0))
	output.append(point + Vector2(0.0, radius))
	output.append(point + Vector2(0.0, -radius))


func _padding_for_bone(bone_name: String) -> float:
	if HEAD_BONES.has(bone_name):
		return skeleton_head_padding_pixels
	if LIMB_END_BONES.has(bone_name):
		return skeleton_limb_padding_pixels
	return skeleton_point_padding_pixels


func _tight_padding_for_bone(bone_name: String) -> float:
	if HEAD_BONES.has(bone_name):
		return minf(skeleton_head_padding_pixels, 14.0)
	if LIMB_END_BONES.has(bone_name):
		return minf(skeleton_limb_padding_pixels, 6.0)
	if bone_name.contains("Shoulder") or bone_name.contains("肩"):
		return minf(skeleton_point_padding_pixels, 8.0)
	return minf(skeleton_point_padding_pixels, 7.0)


func _is_point_inside_capsule_mask(point: Vector2) -> bool:
	for bone_name_variant in _projected_bone_points.keys():
		var bone_name := String(bone_name_variant)
		var radius := _capsule_radius_for_bone(bone_name)
		var bone_point: Vector2 = _projected_bone_points[bone_name]
		if point.distance_squared_to(bone_point) <= radius * radius:
			return true

	for segment in CAPSULE_SEGMENTS:
		var a_name := String(segment[0])
		var b_name := String(segment[1])
		if not _projected_bone_points.has(a_name) or not _projected_bone_points.has(b_name):
			continue
		var a: Vector2 = _projected_bone_points[a_name]
		var b: Vector2 = _projected_bone_points[b_name]
		var radius := maxf(_capsule_radius_for_bone(a_name), _capsule_radius_for_bone(b_name))
		if _distance_squared_to_segment(point, a, b) <= radius * radius:
			return true

	for segment in PMX_CAPSULE_SEGMENTS:
		var a_name := String(segment[0])
		var b_name := String(segment[1])
		if not _projected_bone_points.has(a_name) or not _projected_bone_points.has(b_name):
			continue
		var a: Vector2 = _projected_bone_points[a_name]
		var b: Vector2 = _projected_bone_points[b_name]
		var radius := maxf(_capsule_radius_for_bone(a_name), _capsule_radius_for_bone(b_name))
		if _distance_squared_to_segment(point, a, b) <= radius * radius:
			return true

	return false


func _capsule_radius_for_bone(bone_name: String) -> float:
	if bone_name == "Head" or bone_name == "Neck" or bone_name == "頭" or bone_name == "首":
		return capsule_head_radius_pixels
	if bone_name.ends_with("Hand") or bone_name.ends_with("Foot") or bone_name.begins_with("手首") or bone_name.begins_with("足首") or bone_name.begins_with("つま先"):
		return capsule_hand_foot_radius_pixels
	if bone_name.contains("Arm") or bone_name.contains("Leg") or bone_name.contains("Shoulder") or bone_name.contains("肩") or bone_name.contains("腕") or bone_name.contains("ひじ") or bone_name.begins_with("足.") or bone_name.contains("ひざ"):
		return capsule_limb_radius_pixels
	return capsule_body_radius_pixels


func _distance_squared_to_segment(point: Vector2, a: Vector2, b: Vector2) -> float:
	var ab := b - a
	var length_squared := ab.length_squared()
	if length_squared <= 0.000001:
		return point.distance_squared_to(a)
	var t := clampf((point - a).dot(ab) / length_squared, 0.0, 1.0)
	var closest := a + ab * t
	return point.distance_squared_to(closest)


func _collect_projected_mesh_points(node: Node, output: Array[Vector2]) -> void:
	if node is MeshInstance3D:
		var mesh_instance := node as MeshInstance3D
		if mesh_instance.visible and mesh_instance.is_visible_in_tree() and mesh_instance.mesh != null:
			var aabb := mesh_instance.mesh.get_aabb()
			for local_point in _aabb_points(aabb):
				var world_point := mesh_instance.global_transform * local_point
				if not _camera.is_position_behind(world_point):
					output.append(_camera.unproject_position(world_point))

	for child in node.get_children():
		_collect_projected_mesh_points(child, output)


func _aabb_points(aabb: AABB) -> Array[Vector3]:
	return [
		aabb.position,
		aabb.position + Vector3(aabb.size.x, 0.0, 0.0),
		aabb.position + Vector3(0.0, aabb.size.y, 0.0),
		aabb.position + Vector3(0.0, 0.0, aabb.size.z),
		aabb.position + Vector3(aabb.size.x, aabb.size.y, 0.0),
		aabb.position + Vector3(aabb.size.x, 0.0, aabb.size.z),
		aabb.position + Vector3(0.0, aabb.size.y, aabb.size.z),
		aabb.position + aabb.size,
	]


func _convex_hull(points: Array[Vector2]) -> PackedVector2Array:
	var sorted := points.duplicate()
	sorted.sort_custom(_sort_points_xy)

	var unique: Array[Vector2] = []
	for point in sorted:
		if unique.is_empty() or unique[-1].distance_squared_to(point) > 0.25:
			unique.append(point)
	if unique.size() < 3:
		return PackedVector2Array(unique)

	var lower: Array[Vector2] = []
	for point in unique:
		while lower.size() >= 2 and _cross(lower[-2], lower[-1], point) <= 0.0:
			lower.pop_back()
		lower.append(point)

	var upper: Array[Vector2] = []
	for i in range(unique.size() - 1, -1, -1):
		var point := unique[i]
		while upper.size() >= 2 and _cross(upper[-2], upper[-1], point) <= 0.0:
			upper.pop_back()
		upper.append(point)

	lower.pop_back()
	upper.pop_back()
	var hull := PackedVector2Array()
	for point in lower:
		hull.append(point)
	for point in upper:
		hull.append(point)
	return hull


func _sort_points_xy(a: Vector2, b: Vector2) -> bool:
	if not is_equal_approx(a.x, b.x):
		return a.x < b.x
	return a.y < b.y


func _cross(origin: Vector2, a: Vector2, b: Vector2) -> float:
	return (a.x - origin.x) * (b.y - origin.y) - (a.y - origin.y) * (b.x - origin.x)


func _expand_polygon(source: PackedVector2Array, padding: float, bottom_padding: float) -> PackedVector2Array:
	var center := Vector2.ZERO
	for point in source:
		center += point
	center /= float(source.size())

	var expanded := PackedVector2Array()
	for point in source:
		var direction := point - center
		if direction.length_squared() > 0.0001:
			direction = direction.normalized()
		var extra := padding
		if point.y > center.y:
			extra += bottom_padding
		expanded.append(point + direction * extra)
	return expanded


func _polygons_close(a: PackedVector2Array, b: PackedVector2Array) -> bool:
	if a.size() != b.size():
		return false
	var threshold := min_update_delta_pixels * min_update_delta_pixels
	for i in range(a.size()):
		if a[i].distance_squared_to(b[i]) > threshold:
			return false
	return true


func _draw() -> void:
	if not visible or _projected_bone_points.is_empty():
		return
	var line_color := Color(color.r, color.g, color.b, 0.55)
	var fill_color := Color(color.r, color.g, color.b, 0.18)
	for segment in CAPSULE_SEGMENTS:
		var a_name := String(segment[0])
		var b_name := String(segment[1])
		if not _projected_bone_points.has(a_name) or not _projected_bone_points.has(b_name):
			continue
		var a := to_local(_projected_bone_points[a_name])
		var b := to_local(_projected_bone_points[b_name])
		draw_line(a, b, line_color, 3.0, true)
	for segment in PMX_CAPSULE_SEGMENTS:
		var a_name := String(segment[0])
		var b_name := String(segment[1])
		if not _projected_bone_points.has(a_name) or not _projected_bone_points.has(b_name):
			continue
		var a := to_local(_projected_bone_points[a_name])
		var b := to_local(_projected_bone_points[b_name])
		draw_line(a, b, line_color, 3.0, true)
	for bone_name_variant in _projected_bone_points.keys():
		var bone_name := String(bone_name_variant)
		draw_circle(to_local(_projected_bone_points[bone_name]), _capsule_radius_for_bone(bone_name), fill_color)
