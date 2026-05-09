extends Node

signal motion_applied(slot_name: String)
signal motion_missing(slot_name: String, reason: String)

@export_file("*.json") var motion_slots_path := "res://config/motion_slots.json"
@export var model_loader_path: NodePath
@export var generated_pose_controller_path: NodePath
@export var bind_retry_count := 20
@export var bind_retry_delay_sec := 0.1
@export var apply_initial_motion_on_bind := true
@export var initial_motion := "idle"
@export var prefer_generated_pose_controller := true
@export var stop_animation_players_on_bind := true

var _motion_slots := {}
var _model_root: Node
var _animation_players := []
var _generated_controller: Node
var _bound_model_root: Node
var _last_requested_slot_name := ""
var _last_requested_context: Dictionary = {}
var _replaying_after_bind := false


func _ready() -> void:
	_load_motion_slots()
	_generated_controller = get_node_or_null(generated_pose_controller_path)
	_bind_model_loader()
	call_deferred("_retry_bind_from_loader", 0)


func apply_motion(slot_name: String, context: Dictionary = {}) -> bool:
	if not _replaying_after_bind:
		_last_requested_slot_name = slot_name
		_last_requested_context = context.duplicate(true)

	if not _motion_slots.has(slot_name):
		push_warning("Motion slot is not mapped: %s" % slot_name)
		motion_missing.emit(slot_name, "Motion slot is not mapped.")
		return false

	var slot: Dictionary = _motion_slots[slot_name]
	var applied := false
	var generated_applied := false
	for action in slot.get("actions", []):
		if typeof(action) == TYPE_DICTIONARY:
			var action_type := str(action.get("type", "animation"))
			if prefer_generated_pose_controller and generated_applied and action_type == "animation":
				continue
			var action_applied := _apply_action(action, context)
			applied = action_applied or applied
			if action_type == "generated_controller" and action_applied:
				generated_applied = true

	if applied:
		print("Pet pose motion applied: %s" % slot_name)
		motion_applied.emit(slot_name)
	else:
		push_warning("Pet pose motion did not apply: %s" % slot_name)
		motion_missing.emit(slot_name, "No configured action could be applied.")
	return applied


func set_model_root(model_root: Node) -> void:
	_model_root = model_root
	_animation_players.clear()
	if _model_root != null:
		_collect_animation_players(_model_root)
		if stop_animation_players_on_bind:
			_stop_animation_players()
	_bind_generated_controller_to_model()


func _bind_model_loader() -> void:
	if model_loader_path.is_empty():
		return

	var loader := get_node_or_null(model_loader_path)
	if loader == null:
		return

	if loader.has_signal("model_loaded"):
		var callback := Callable(self, "_on_model_loaded")
		if not loader.is_connected("model_loaded", callback):
			loader.connect("model_loaded", callback)
	if loader.has_method("get_model_root"):
		var model_root = loader.call("get_model_root")
		if model_root != null:
			set_model_root(model_root)


func _on_model_loaded(model_root: Node) -> void:
	set_model_root(model_root)


func _retry_bind_from_loader(attempt: int) -> void:
	if _bound_model_root != null and is_instance_valid(_bound_model_root):
		return
	if model_loader_path.is_empty():
		return

	var loader := get_node_or_null(model_loader_path)
	if loader != null and loader.has_method("get_model_root"):
		var model_root = loader.call("get_model_root")
		if model_root != null:
			set_model_root(model_root)
			if _bound_model_root != null and is_instance_valid(_bound_model_root):
				return

	if attempt >= bind_retry_count:
		push_warning("PoseController could not bind generated poses after %d attempts." % bind_retry_count)
		return

	await get_tree().create_timer(bind_retry_delay_sec).timeout
	_retry_bind_from_loader(attempt + 1)


func _apply_action(action: Dictionary, context: Dictionary) -> bool:
	var action_type := str(action.get("type", "animation"))
	match action_type:
		"animation":
			return _play_animation_action(action)
		"generated_controller":
			return _call_generated_controller(action)
		"log":
			print(str(action.get("message", "")))
			return true
		_:
			motion_missing.emit(str(context.get("state", "")), "Unknown motion action type: %s" % action_type)
			return false


func _play_animation_action(action: Dictionary) -> bool:
	var candidates := _animation_candidates(action)
	if candidates.is_empty():
		return false

	for player in _animation_players:
		if not is_instance_valid(player):
			continue
		var animation_name := _find_animation_name(player, candidates)
		if not animation_name.is_empty():
			var blend_sec := float(action.get("blend_sec", -1.0))
			if blend_sec >= 0.0:
				player.play(animation_name, blend_sec)
			else:
				player.play(animation_name)
			return true
	return false


func _call_generated_controller(action: Dictionary) -> bool:
	var controller := _generated_controller
	if controller == null and not generated_pose_controller_path.is_empty():
		controller = get_node_or_null(generated_pose_controller_path)
		_generated_controller = controller
	if controller == null:
		push_warning("Generated pose controller is missing: %s" % str(generated_pose_controller_path))
		return false

	var method_name := str(action.get("method", ""))
	if method_name.is_empty() or not controller.has_method(method_name):
		push_warning("Generated pose controller method missing: %s" % method_name)
		return false

	var args = action.get("args", [])
	if typeof(args) != TYPE_ARRAY:
		args = []
	controller.callv(method_name, args)
	return true


func _bind_generated_controller_to_model() -> void:
	if _model_root == null:
		return
	if _bound_model_root == _model_root:
		return

	var controller := _generated_controller
	if controller == null and not generated_pose_controller_path.is_empty():
		controller = get_node_or_null(generated_pose_controller_path)
		_generated_controller = controller
	if controller == null:
		return

	if controller.has_method("bind_external_character_model"):
		var bound := bool(controller.call("bind_external_character_model", _model_root))
		if bound:
			_bound_model_root = _model_root
			print("Generated pose controller bound to runtime model.")
			if not _last_requested_slot_name.is_empty() and not _replaying_after_bind:
				_replaying_after_bind = true
				call_deferred("_replay_last_motion_after_bind")
			elif apply_initial_motion_on_bind and not initial_motion.is_empty():
				_last_requested_slot_name = initial_motion
				_last_requested_context = {"state": initial_motion}
				_replaying_after_bind = true
				call_deferred("_replay_last_motion_after_bind")
		else:
			var reason := "Generated controller could not bind to runtime model."
			if controller.has_method("get_last_error"):
				reason = str(controller.call("get_last_error"))
			motion_missing.emit("generated_controller", reason)
			push_warning(reason)


func _replay_last_motion_after_bind() -> void:
	if _last_requested_slot_name.is_empty():
		_replaying_after_bind = false
		return
	apply_motion(_last_requested_slot_name, _last_requested_context)
	_replaying_after_bind = false


func _animation_candidates(action: Dictionary) -> Array:
	var candidates := []
	if action.has("name"):
		candidates.append(str(action["name"]))
	for key in ["names", "fallback_names"]:
		for item in action.get(key, []):
			candidates.append(str(item))
	return candidates


func _find_animation_name(player: AnimationPlayer, candidates: Array) -> String:
	var available := {}
	for animation_name in player.get_animation_list():
		available[_normalize_name(str(animation_name))] = str(animation_name)

	for candidate in candidates:
		var normalized := _normalize_name(str(candidate))
		if available.has(normalized):
			return available[normalized]
	return ""


func _collect_animation_players(node: Node) -> void:
	if node is AnimationPlayer:
		_animation_players.append(node)
	for child in node.get_children():
		_collect_animation_players(child)


func _stop_animation_players() -> void:
	for player in _animation_players:
		if is_instance_valid(player) and player is AnimationPlayer:
			(player as AnimationPlayer).stop()


func _normalize_name(value: String) -> String:
	return value.to_lower().replace(" ", "").replace("_", "").replace("-", "")


func _load_motion_slots() -> void:
	var data := _load_json(motion_slots_path)
	_motion_slots = data.get("slots", {})
	if typeof(_motion_slots) != TYPE_DICTIONARY:
		_motion_slots = {}


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


func get_debug_snapshot() -> Dictionary:
	var generated_snapshot := {}
	if _generated_controller != null and _generated_controller.has_method("get_debug_snapshot"):
		var snapshot = _generated_controller.call("get_debug_snapshot")
		if typeof(snapshot) == TYPE_DICTIONARY:
			generated_snapshot = snapshot

	return {
		"motion_slot_count": _motion_slots.size(),
		"model_root": str(_model_root.get_path()) if _model_root != null and is_instance_valid(_model_root) else "",
		"bound_model_root": str(_bound_model_root.get_path()) if _bound_model_root != null and is_instance_valid(_bound_model_root) else "",
		"animation_player_count": _animation_players.size(),
		"last_requested_slot": _last_requested_slot_name,
		"generated_controller": str(_generated_controller.get_path()) if _generated_controller != null and is_instance_valid(_generated_controller) else "",
		"generated": generated_snapshot,
	}
