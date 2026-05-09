extends Node

signal expression_changed(expression_name: String, weight: float)
signal expression_missing(expression_name: String)

@export_file("*.json") var expression_map_path := "res://config/expression_map.json"
@export var model_loader_path: NodePath

var _expression_map: Dictionary = {}
var _expressions: Dictionary = {}
var _model_root: Node
var _mesh_cache: Array = []


func _ready() -> void:
	_load_expression_map()
	_bind_model_loader()


func set_model_root(model_root: Node) -> void:
	_model_root = model_root
	_rebuild_mesh_cache()


func set_expression(expression_name: String, weight: float = 1.0) -> bool:
	if not _expressions.has(expression_name):
		expression_missing.emit(expression_name)
		push_warning("Expression is not mapped: %s" % expression_name)
		return false

	var expression_data: Dictionary = _expressions[expression_name]
	var exclusive_group := str(expression_data.get("exclusive_group", "face"))
	if bool(expression_data.get("reset_others", true)):
		_reset_expression_group(exclusive_group)

	_apply_expression_data(expression_data, clampf(weight, 0.0, 1.0))
	expression_changed.emit(expression_name, weight)
	return true


func set_expression_weight(expression_name: String, weight: float) -> bool:
	if not _expressions.has(expression_name):
		expression_missing.emit(expression_name)
		return false

	var expression_data: Dictionary = _expressions[expression_name]
	_apply_expression_data(expression_data, clampf(weight, 0.0, 1.0))
	expression_changed.emit(expression_name, weight)
	return true


func get_lip_sync_expression_name() -> String:
	var lip_sync = _expression_map.get("lip_sync", {})
	if typeof(lip_sync) != TYPE_DICTIONARY:
		return ""
	return str(lip_sync.get("expression", ""))


func _bind_model_loader() -> void:
	if model_loader_path.is_empty():
		return

	var loader := get_node_or_null(model_loader_path)
	if loader == null:
		return

	if loader.has_signal("model_loaded"):
		loader.model_loaded.connect(_on_model_loaded)
	if loader.has_method("get_model_root"):
		var model_root = loader.call("get_model_root")
		if model_root != null:
			set_model_root(model_root)


func _on_model_loaded(model_root: Node) -> void:
	set_model_root(model_root)


func _load_expression_map() -> void:
	_expression_map = _load_json(expression_map_path)
	_expressions = _expression_map.get("expressions", {})
	if typeof(_expressions) != TYPE_DICTIONARY:
		_expressions = {}


func _rebuild_mesh_cache() -> void:
	_mesh_cache.clear()
	if _model_root != null:
		_collect_meshes(_model_root)


func _collect_meshes(node: Node) -> void:
	if node is MeshInstance3D:
		_mesh_cache.append(node)
	for child in node.get_children():
		_collect_meshes(child)


func _reset_expression_group(group_name: String) -> void:
	for expression_name in _expressions.keys():
		var expression_data: Dictionary = _expressions[expression_name]
		if str(expression_data.get("exclusive_group", "face")) != group_name:
			continue
		for target in expression_data.get("blend_shapes", []):
			if typeof(target) == TYPE_DICTIONARY:
				_apply_blend_shape_target(target, 0.0)


func _apply_expression_data(expression_data: Dictionary, normalized_weight: float) -> void:
	for target in expression_data.get("blend_shapes", []):
		if typeof(target) == TYPE_DICTIONARY:
			_apply_blend_shape_target(target, normalized_weight)


func _apply_blend_shape_target(target: Dictionary, normalized_weight: float) -> void:
	var names := _target_names(target)
	if names.is_empty():
		return

	var target_weight := clampf(float(target.get("weight", 1.0)) * normalized_weight, 0.0, 1.0)
	for mesh_instance in _mesh_cache:
		if not is_instance_valid(mesh_instance):
			continue
		_apply_blend_shape_names(mesh_instance, names, target_weight)


func _apply_blend_shape_names(mesh_instance: MeshInstance3D, names: Array, weight: float) -> void:
	if mesh_instance.mesh == null:
		return

	var count: int = mesh_instance.mesh.get_blend_shape_count()
	for index in range(count):
		var shape_name := str(mesh_instance.mesh.get_blend_shape_name(index))
		for target_name in names:
			if _names_match(shape_name, str(target_name)):
				mesh_instance.set_blend_shape_value(index, weight)
				return


func _target_names(target: Dictionary) -> Array:
	var names := []
	if target.has("name"):
		names.append(str(target["name"]))
	for item in target.get("names", []):
		names.append(str(item))
	return names


func _names_match(actual: String, expected: String) -> bool:
	return _normalize_name(actual) == _normalize_name(expected)


func _normalize_name(value: String) -> String:
	return value.to_lower().replace(" ", "").replace("_", "").replace("-", "")


func _load_json(path: String) -> Dictionary:
	if not FileAccess.file_exists(path):
		push_warning("JSON file does not exist: %s" % path)
		return {}

	var file := FileAccess.open(path, FileAccess.READ)
	if file == null:
		push_warning("Could not open JSON file: %s" % path)
		return {}

	var parsed = JSON.parse_string(file.get_as_text())
	if typeof(parsed) != TYPE_DICTIONARY:
		push_warning("Invalid JSON dictionary: %s" % path)
		return {}
	return parsed
