extends Node3D

signal model_loaded(model_root: Node)
signal model_load_failed(path: String, reason: String)
signal accessory_loaded(accessory_name: String, accessory_root: Node)
signal accessory_load_failed(accessory_name: String, path: String, reason: String)

@export var runtime_asset_dir := "res://assets/converted"
@export_file("*.json") var config_path := "res://config/pet_config.json"
@export_file("*.glb", "*.vrm") var default_model_path := "res://assets/converted/user_pet_model.glb"
@export_file("*.glb") var fallback_glb_path := ""
@export var load_on_ready := true
@export var model_scale := Vector3.ONE
@export var model_rotation_degrees := Vector3.ZERO
@export var model_position := Vector3.ZERO
@export var show_placeholder_on_failure := true
@export var auto_fit_to_view := true
@export var target_height := 2.45
@export var target_center_y := 1.08
@export var target_center_z := -0.35
@export var load_accessories := true

var _current_model: Node
var _accessory_configs: Array[Dictionary] = []
var _loaded_accessories: Dictionary = {}


func _ready() -> void:
	_apply_config()
	if load_on_ready:
		var path: String = default_model_path
		if path.is_empty():
			path = _find_first_runtime_asset()
		load_model(path)


func load_model(path: String) -> bool:
	return _load_model_internal(path, true)


func _load_model_internal(path: String, allow_configured_fallback: bool) -> bool:
	var normalized_path := _normalize_path(path)
	if not _is_runtime_asset_path(normalized_path):
		_fail_with_placeholder(normalized_path, "Model path must be a .glb or .vrm under assets/converted.")
		return false

	if not ResourceLoader.exists(normalized_path) and not FileAccess.file_exists(normalized_path):
		if allow_configured_fallback and _try_fallback_glb(normalized_path, "Resource does not exist."):
			return true
		_fail_with_placeholder(normalized_path, "Resource does not exist. Convert or copy a model into assets/converted.")
		return false

	if normalized_path.get_extension().to_lower() == "vrm" and not ResourceLoader.exists(normalized_path):
		var reason := "Godot VRM importer is not installed or has not imported this VRM yet."
		if allow_configured_fallback and _try_fallback_glb(normalized_path, reason):
			return true
		_fail_with_placeholder(normalized_path, reason)
		return false

	var packed_scene = ResourceLoader.load(normalized_path)
	if packed_scene == null or not (packed_scene is PackedScene):
		var extension := normalized_path.get_extension().to_lower()
		var reason := "Resource is not a loadable PackedScene. Check GLB import or VRM importer support."
		if extension == "vrm":
			reason = "VRM is reserved, but a Godot VRM importer is required before runtime loading."
		if allow_configured_fallback and _try_fallback_glb(normalized_path, reason):
			return true
		_fail_with_placeholder(normalized_path, reason)
		return false

	_clear_current_model()
	var scene := packed_scene as PackedScene
	_current_model = scene.instantiate()
	add_child(_current_model)
	_apply_model_transform(_current_model)
	if auto_fit_to_view:
		_fit_model_to_view(_current_model)
	_load_configured_accessories()
	model_loaded.emit(_current_model)
	return true


func get_model_root() -> Node:
	return _current_model


func set_accessory_visible(accessory_name: String, visible: bool) -> bool:
	var accessory = _loaded_accessories.get(accessory_name)
	if accessory == null or not is_instance_valid(accessory):
		return false
	if accessory is Node3D:
		(accessory as Node3D).visible = visible
	return true


func reload_default_model() -> bool:
	_apply_config()
	return load_model(default_model_path)


func _clear_current_model() -> void:
	if _current_model != null and is_instance_valid(_current_model):
		remove_child(_current_model)
		_current_model.queue_free()
	_current_model = null
	_loaded_accessories.clear()


func _apply_model_transform(model_root: Node) -> void:
	if model_root is Node3D:
		var node_3d := model_root as Node3D
		node_3d.position = model_position
		node_3d.rotation_degrees = model_rotation_degrees
		node_3d.scale = model_scale


func _apply_config() -> void:
	var config := _load_json(config_path)
	var model_config = config.get("model", {})
	if typeof(model_config) != TYPE_DICTIONARY:
		return

	runtime_asset_dir = _normalize_path(str(model_config.get("runtime_asset_dir", runtime_asset_dir)))
	default_model_path = _normalize_path(str(model_config.get("path", default_model_path)))
	fallback_glb_path = _normalize_path(str(model_config.get("fallback_glb_path", fallback_glb_path)))
	model_position = _vector3_from_array(model_config.get("position", []), model_position)
	model_rotation_degrees = _vector3_from_array(model_config.get("rotation_degrees", []), model_rotation_degrees)
	model_scale = _vector3_from_array(model_config.get("scale", []), model_scale)
	show_placeholder_on_failure = bool(model_config.get("fallback_placeholder", show_placeholder_on_failure))
	auto_fit_to_view = bool(model_config.get("auto_fit_to_view", auto_fit_to_view))
	target_height = float(model_config.get("target_height", target_height))
	target_center_y = float(model_config.get("target_center_y", target_center_y))
	target_center_z = float(model_config.get("target_center_z", target_center_z))

	_accessory_configs.clear()
	var accessories_config = config.get("accessories", [])
	if typeof(accessories_config) == TYPE_ARRAY:
		for accessory in accessories_config:
			if typeof(accessory) == TYPE_DICTIONARY:
				_accessory_configs.append((accessory as Dictionary).duplicate(true))


func _load_configured_accessories() -> void:
	_loaded_accessories.clear()
	if not load_accessories or _current_model == null:
		return

	for accessory_config in _accessory_configs:
		var path := _normalize_path(str(accessory_config.get("path", "")))
		var accessory_name := str(accessory_config.get("name", path.get_file().get_basename()))
		if accessory_name.is_empty():
			accessory_name = path.get_file().get_basename()
		if path.is_empty():
			_emit_accessory_failed(accessory_name, path, "Accessory path is empty.")
			continue
		if not _is_runtime_asset_path(path):
			_emit_accessory_failed(accessory_name, path, "Accessory path must be a .glb or .vrm under assets/converted.")
			continue
		if not ResourceLoader.exists(path) and not FileAccess.file_exists(path):
			_emit_accessory_failed(accessory_name, path, "Accessory resource does not exist.")
			continue

		var packed_scene = ResourceLoader.load(path)
		if packed_scene == null or not (packed_scene is PackedScene):
			_emit_accessory_failed(accessory_name, path, "Accessory is not a loadable PackedScene.")
			continue

		var accessory_root := (packed_scene as PackedScene).instantiate()
		accessory_root.name = "Accessory_%s" % accessory_name
		var parent := _resolve_accessory_parent(str(accessory_config.get("attach_to", "")))
		parent.add_child(accessory_root)
		_apply_accessory_transform(accessory_root, accessory_config)
		_loaded_accessories[accessory_name] = accessory_root
		accessory_loaded.emit(accessory_name, accessory_root)


func _apply_accessory_transform(accessory_root: Node, accessory_config: Dictionary) -> void:
	if not (accessory_root is Node3D):
		return

	var node_3d := accessory_root as Node3D
	node_3d.position = _vector3_from_array(accessory_config.get("position", []), Vector3.ZERO)
	node_3d.rotation_degrees = _vector3_from_array(accessory_config.get("rotation_degrees", []), Vector3.ZERO)
	node_3d.scale = _vector3_from_array(accessory_config.get("scale", []), Vector3.ONE)
	node_3d.visible = bool(accessory_config.get("visible", false))


func _resolve_accessory_parent(attach_to: String) -> Node:
	if _current_model == null:
		return self
	if attach_to.is_empty():
		return _current_model

	var node_path := NodePath(attach_to)
	var direct := _current_model.get_node_or_null(node_path)
	if direct != null:
		return direct

	var found := _find_child_by_name(_current_model, attach_to)
	if found != null:
		return found

	push_warning("Accessory attach target not found, using model root: %s" % attach_to)
	return _current_model


func _find_child_by_name(node: Node, child_name: String) -> Node:
	for child in node.get_children():
		if child.name == child_name:
			return child
		var nested := _find_child_by_name(child, child_name)
		if nested != null:
			return nested
	return null


func _find_first_runtime_asset() -> String:
	var dir_path := _normalized_runtime_dir()
	var dir: DirAccess = DirAccess.open(dir_path)
	if dir == null:
		return ""

	dir.list_dir_begin()
	var file_name: String = dir.get_next()
	while not file_name.is_empty():
		if not dir.current_is_dir():
			var candidate := "%s/%s" % [dir_path, file_name]
			if _is_runtime_asset_path(candidate):
				dir.list_dir_end()
				return candidate
		file_name = dir.get_next()
	dir.list_dir_end()
	return ""


func _is_runtime_asset_path(path: String) -> bool:
	var dir_path := _normalized_runtime_dir()
	var normalized_path := _normalize_path(path)
	var extension := normalized_path.get_extension().to_lower()
	return normalized_path.begins_with(dir_path + "/") and extension in ["glb", "vrm"]


func _normalized_runtime_dir() -> String:
	var dir_path := _normalize_path(runtime_asset_dir)
	if dir_path.ends_with("/"):
		dir_path = dir_path.substr(0, dir_path.length() - 1)
	return dir_path


func _normalize_path(path: String) -> String:
	return path.replace("\\", "/")


func _emit_load_failed(path: String, reason: String) -> void:
	push_warning("Pet model load failed: %s (%s)" % [path, reason])
	model_load_failed.emit(path, reason)


func _emit_accessory_failed(accessory_name: String, path: String, reason: String) -> void:
	push_warning("Pet accessory load failed: %s %s (%s)" % [accessory_name, path, reason])
	accessory_load_failed.emit(accessory_name, path, reason)


func _fail_with_placeholder(path: String, reason: String) -> void:
	_emit_load_failed(path, reason)
	if show_placeholder_on_failure:
		_show_placeholder(reason)


func _try_fallback_glb(failed_path: String, reason: String) -> bool:
	if fallback_glb_path.is_empty():
		return false

	var normalized_fallback := _normalize_path(fallback_glb_path)
	if normalized_fallback == failed_path:
		return false
	if not _is_runtime_asset_path(normalized_fallback) or normalized_fallback.get_extension().to_lower() != "glb":
		return false
	if not ResourceLoader.exists(normalized_fallback) and not FileAccess.file_exists(normalized_fallback):
		return false

	push_warning("Primary pet model failed, trying fallback GLB: %s (%s)" % [failed_path, reason])
	return _load_model_internal(normalized_fallback, false)


func _show_placeholder(reason: String) -> void:
	_clear_current_model()

	var placeholder_root := Node3D.new()
	placeholder_root.name = "ModelLoadPlaceholder"

	var mesh_instance := MeshInstance3D.new()
	mesh_instance.name = "FallbackCube"
	mesh_instance.mesh = BoxMesh.new()
	mesh_instance.position = Vector3(0.0, 0.65, 0.0)
	mesh_instance.scale = Vector3(0.65, 1.3, 0.35)

	var material := StandardMaterial3D.new()
	material.albedo_color = Color(0.2, 0.7, 1.0, 0.92)
	material.roughness = 0.55
	mesh_instance.material_override = material

	placeholder_root.add_child(mesh_instance)
	add_child(placeholder_root)
	_current_model = placeholder_root
	_apply_model_transform(_current_model)
	if auto_fit_to_view:
		_fit_model_to_view(_current_model)
	model_loaded.emit(_current_model)
	print("Showing fallback pet placeholder: %s" % reason)


func _load_json(path: String) -> Dictionary:
	if path.is_empty() or not FileAccess.file_exists(path):
		return {}

	var file := FileAccess.open(path, FileAccess.READ)
	if file == null:
		return {}

	var parsed = JSON.parse_string(file.get_as_text())
	if typeof(parsed) != TYPE_DICTIONARY:
		push_warning("Invalid JSON dictionary: %s" % path)
		return {}
	return parsed


func _vector3_from_array(value, fallback: Vector3) -> Vector3:
	if typeof(value) != TYPE_ARRAY or value.size() < 3:
		return fallback
	return Vector3(float(value[0]), float(value[1]), float(value[2]))


func _fit_model_to_view(model_root: Node) -> void:
	if not (model_root is Node3D):
		return

	var meshes: Array[MeshInstance3D] = []
	_collect_meshes(model_root, meshes)
	var bounds := _merged_mesh_bounds(meshes)
	if bounds.size == Vector3.ZERO:
		return

	var node_3d := model_root as Node3D
	var height := maxf(bounds.size.y, 0.001)
	var fit_scale := target_height / height
	node_3d.scale *= fit_scale
	node_3d.force_update_transform()
	meshes.clear()
	_collect_meshes(model_root, meshes)
	bounds = _merged_mesh_bounds(meshes)
	if bounds.size == Vector3.ZERO:
		return

	var center := bounds.get_center()
	node_3d.global_position += Vector3(
		-center.x,
		target_center_y - center.y,
		target_center_z - center.z
	)


func _collect_meshes(node: Node, meshes: Array[MeshInstance3D]) -> void:
	if node is MeshInstance3D:
		meshes.append(node)
	for child in node.get_children():
		_collect_meshes(child, meshes)


func _merged_mesh_bounds(meshes: Array[MeshInstance3D]) -> AABB:
	var merged := AABB()
	var has_bounds := false
	for mesh_instance in meshes:
		if mesh_instance.mesh == null:
			continue
		var world := _aabb_to_world(mesh_instance.global_transform, mesh_instance.mesh.get_aabb())
		if not has_bounds:
			merged = world
			has_bounds = true
		else:
			merged = merged.merge(world)
	return merged if has_bounds else AABB()


func _aabb_to_world(transform: Transform3D, aabb: AABB) -> AABB:
	var points := [
		aabb.position,
		aabb.position + Vector3(aabb.size.x, 0.0, 0.0),
		aabb.position + Vector3(0.0, aabb.size.y, 0.0),
		aabb.position + Vector3(0.0, 0.0, aabb.size.z),
		aabb.position + Vector3(aabb.size.x, aabb.size.y, 0.0),
		aabb.position + Vector3(aabb.size.x, 0.0, aabb.size.z),
		aabb.position + Vector3(0.0, aabb.size.y, aabb.size.z),
		aabb.position + aabb.size,
	]
	var result := AABB(transform * points[0], Vector3.ZERO)
	for point in points:
		result = result.expand(transform * point)
	return result
