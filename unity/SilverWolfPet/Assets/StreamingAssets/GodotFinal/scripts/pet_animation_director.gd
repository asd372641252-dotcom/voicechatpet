extends Node

signal action_started(action_name: String, posture: String, xfade_time: float)
signal action_queued(action_name: String)
signal route_started(target_action: String, route: Array)

@export_file("*.json") var action_config_path := "res://config/pet_animation_actions.json"
@export_file("*.json") var semantic_pose_map_path := "res://config/pet_pose_semantic_map.json"
@export_file("*.json") var desktop_action_taxonomy_path := "res://config/pet_desktop_action_taxonomy.json"
@export var action_source_path: NodePath = NodePath("..")
@export var animation_tree_path: NodePath
@export var auto_create_animation_tree := true
@export var auto_start_default := false
@export var default_action_name := "KA_Idle01_breathing"
@export var fallback_xfade_time := 0.28
@export var process_queue := true
@export var debug_logging := true
@export var use_transition_routes := false
@export var enforce_min_hold := false
@export var enforce_uninterruptible := true
@export var defer_pose_requests_until_action_ready := true
@export var pose_interrupt_min_visible_sec := 2.8
@export var pose_priority_bypass_defer := 100
@export var only_idle_actions := true
@export var excluded_idle_action_numbers := PackedInt32Array([7, 13, 30, 33, 34, 57, 58])
@export var skip_same_pose_action := true
@export var pose_switch_cooldown_sec := 0.0

var _action_source: Node
var _animation_tree: AnimationTree
var _playback: AnimationNodeStateMachinePlayback
var _tree_root: AnimationNodeStateMachine
var _actions: Dictionary = {}
var _semantic_map: Dictionary = {}
var _semantic_state_actions: Dictionary = {}
var _semantic_emotion_actions: Dictionary = {}
var _semantic_gesture_actions: Dictionary = {}
var _semantic_posture_defaults: Dictionary = {}
var _semantic_transition_routes: Dictionary = {}
var _routes: Dictionary = {}
var _groups: Dictionary = {}
var _safe_state_actions: Dictionary = {}
var _current_action := ""
var _current_posture := "stand"
var _current_type := "loop"
var _current_interruptible := true
var _current_min_hold := 0.0
var _current_elapsed := 0.0
var _route_queue: Array[String] = []
var _pending_action := ""
var _pending_action_priority := -999999
var _last_travel_frame := -1
var _last_pose_switch_at_msec := 0
var _recent_pose_actions: Array[String] = []
var _rng := RandomNumberGenerator.new()

const GROUP_FOR_POSTURE := {
	"stand": "StandGroup",
	"sit": "SitGroup",
	"lie": "LieGroup",
	"air": "AirGroup",
}


func _ready() -> void:
	_rng.randomize()
	_load_action_config()
	_load_semantic_pose_map()
	_action_source = get_node_or_null(action_source_path)
	_ensure_animation_tree()
	if default_action_name.is_empty() and _actions.has("KA_Idle01_breathing"):
		default_action_name = "KA_Idle01_breathing"
	if auto_start_default and not default_action_name.is_empty():
		request_action(default_action_name, true)


func _process(delta: float) -> void:
	_current_elapsed += delta
	if process_queue:
		_process_route_queue()


func request_action(action_name: String, force := false, manual := false) -> bool:
	if not _actions.has(action_name):
		push_warning("AnimationDirector unknown action: %s" % action_name)
		return false
	if force or manual:
		_pending_action = ""
		_pending_action_priority = -999999
		_route_queue.clear()
		_start_action(action_name)
		return true
	if not force and not _can_interrupt_now(manual):
		_queue_pending_action(action_name, 0)
		return true
	var route := _compute_route(action_name)
	if route.is_empty():
		return false
	_route_queue.clear()
	for item in route:
		_route_queue.append(String(item))
	route_started.emit(action_name, route)
	_process_route_queue(true)
	return true


func request_pose(command: Dictionary) -> bool:
	var target_action := _resolve_pose_action(command)
	if target_action.is_empty():
		target_action = default_action_name
	if not _actions.has(target_action):
		push_warning("AnimationDirector pose target action not found: %s" % target_action)
		return false
	if skip_same_pose_action and target_action == _current_action:
		if debug_logging:
			print("AnimationDirector skip same pose action=%s" % target_action)
		return true
	var now_msec := Time.get_ticks_msec()
	if pose_switch_cooldown_sec > 0.0 and _last_pose_switch_at_msec > 0:
		var elapsed_sec := float(now_msec - _last_pose_switch_at_msec) / 1000.0
		if elapsed_sec < pose_switch_cooldown_sec:
			if debug_logging:
				print("AnimationDirector pose cooldown action=%s current=%s elapsed=%.2f" % [target_action, _current_action, elapsed_sec])
			return true

	var priority := int(command.get("priority", 0))
	if _should_defer_pose_action(command, target_action, priority):
		_queue_pending_action(target_action, priority)
		return true

	var target_posture := _sanitize_posture(str(command.get("posture", _current_posture)))
	var route := _compute_semantic_pose_route(target_posture, target_action)
	if route.is_empty():
		return false

	_last_pose_switch_at_msec = now_msec
	_pending_action = ""
	_pending_action_priority = -999999
	_route_queue.clear()
	for item in route:
		_route_queue.append(String(item))
	route_started.emit(target_action, route)
	_process_route_queue()
	return true


func request_state(state_name: String) -> bool:
	var actions: Array = _safe_state_actions.get(state_name, [])
	if actions.is_empty():
		push_warning("AnimationDirector state has no safe actions: %s" % state_name)
		return false
	return request_action(String(actions[0]))


func get_current_action() -> String:
	return _current_action


func get_current_posture() -> String:
	return _current_posture


func get_action_data(action_name: String) -> Dictionary:
	return _actions.get(action_name, {})


func sync_current_action(action_name: String) -> void:
	if not _actions.has(action_name):
		return
	_set_current_action_state(action_name)


func _can_interrupt_now(ignore_min_hold := false) -> bool:
	if _current_action.is_empty():
		return true
	if not ignore_min_hold and defer_pose_requests_until_action_ready and _action_source != null and _action_source.has_method("can_interrupt_action_for_director"):
		if not bool(_action_source.call("can_interrupt_action_for_director", pose_interrupt_min_visible_sec)):
			return false
	if enforce_uninterruptible and not _current_interruptible and _current_elapsed < maxf(_current_min_hold, 0.1):
		return false
	if ignore_min_hold or not enforce_min_hold:
		return true
	return _current_elapsed >= _current_min_hold


func _process_route_queue(ignore_hold := false) -> void:
	if _route_queue.is_empty():
		if not _pending_action.is_empty() and _can_interrupt_now():
			var next: String = _pending_action
			_pending_action = ""
			_pending_action_priority = -999999
			request_action(next)
		return
	if not ignore_hold and not _can_interrupt_now():
		return
	var next_action: String = String(_route_queue.pop_front())
	_start_action(next_action)


func _start_action(action_name: String) -> void:
	var data: Dictionary = _actions.get(action_name, {})
	if data.is_empty():
		return
	_set_current_action_state(action_name)
	var xfade := float(data.get("default_xfade_time", fallback_xfade_time))
	_travel_animation_tree(action_name, _current_posture, xfade)
	_call_action_source(action_name, xfade)
	action_started.emit(action_name, _current_posture, xfade)
	if debug_logging:
		print("AnimationDirector start action=%s posture=%s type=%s xfade=%.2f" % [action_name, _current_posture, _current_type, xfade])


func _set_current_action_state(action_name: String) -> void:
	var data: Dictionary = _actions.get(action_name, {})
	if data.is_empty():
		return
	_current_action = action_name
	_current_posture = String(data.get("posture", "stand"))
	_current_type = String(data.get("type", "loop"))
	_current_interruptible = bool(data.get("interruptible", true))
	_current_min_hold = float(data.get("min_hold_time", 0.0))
	_current_elapsed = 0.0
	_remember_recent_action(action_name)


func _compute_route(target_action: String) -> Array:
	var target_data: Dictionary = _actions[target_action]
	var target_posture := String(target_data.get("posture", "stand"))
	if not use_transition_routes or _current_action.is_empty() or _current_posture == target_posture:
		return [target_action]
	var key := "%s->%s" % [_current_posture, target_posture]
	if _routes.has(key):
		var route: Array = _routes[key]
		if not route.is_empty() and String(route[route.size() - 1]) != target_action:
			var routed := route.duplicate()
			routed.append(target_action)
			return _dedupe_adjacent(routed)
		return _dedupe_adjacent(route)
	return [target_action]


func _compute_semantic_pose_route(target_posture: String, target_action: String) -> Array:
	if _current_action.is_empty() or _current_posture == target_posture:
		return [target_action]

	var key := "%s->%s" % [_current_posture, target_posture]
	var route: Array = _semantic_transition_routes.get(key, [])
	var output := []
	for item in route:
		var action := String(item)
		if _actions.has(action):
			output.append(action)
	if output.is_empty() and _routes.has(key):
		for item in (_routes[key] as Array):
			var action := String(item)
			if _actions.has(action):
				output.append(action)
	if output.is_empty():
		output.append(target_action)
	elif String(output[output.size() - 1]) != target_action:
		output.append(target_action)
	return _dedupe_adjacent(output)


func _dedupe_adjacent(route: Array) -> Array:
	var output := []
	var previous := ""
	for item in route:
		var action := String(item)
		if action.is_empty() or action == previous:
			continue
		if _actions.has(action):
			output.append(action)
			previous = action
	return output


func _travel_animation_tree(action_name: String, posture: String, _xfade_time: float) -> void:
	if _playback == null:
		return
	var frame := Engine.get_process_frames()
	if _last_travel_frame == frame:
		return
	_last_travel_frame = frame
	var group := String(GROUP_FOR_POSTURE.get(posture, "StandGroup"))
	_playback.travel(group)


func _call_action_source(action_name: String, xfade_time: float) -> void:
	if _action_source == null:
		return
	_action_source.set("action_transition_sec", xfade_time)
	if _action_source.has_method("request_action"):
		_action_source.call("request_action", action_name)


func _should_defer_pose_action(command: Dictionary, target_action: String, priority: int) -> bool:
	if not defer_pose_requests_until_action_ready:
		return false
	if priority >= pose_priority_bypass_defer:
		return false
	if _current_action.is_empty() or target_action == _current_action:
		return false
	if bool(command.get("overlay_only", false)):
		return false
	return not _can_interrupt_now()


func _queue_pending_action(action_name: String, priority: int) -> void:
	if action_name.is_empty():
		return
	if not _pending_action.is_empty() and priority < _pending_action_priority:
		if debug_logging:
			print("AnimationDirector keep queued action=%s skip=%s priority=%d current_priority=%d" % [_pending_action, action_name, priority, _pending_action_priority])
		return
	_pending_action = action_name
	_pending_action_priority = priority
	action_queued.emit(action_name)
	if debug_logging:
		print("AnimationDirector queued action=%s current=%s priority=%d" % [action_name, _current_action, priority])


func _load_action_config() -> void:
	var parsed := _load_json_dict(action_config_path)
	if parsed.is_empty():
		return
	default_action_name = String(parsed.get("default_action", default_action_name))
	_routes = parsed.get("transition_routes", {})
	_groups = parsed.get("groups", {})
	_safe_state_actions = parsed.get("safe_state_actions", {})
	_actions.clear()
	var actions: Array = parsed.get("actions", [])
	for item in actions:
		if typeof(item) != TYPE_DICTIONARY:
			continue
		var data: Dictionary = item
		var name := String(data.get("name", ""))
		if name.is_empty():
			continue
		if not _is_action_allowed(name):
			continue
		_actions[name] = data

	if only_idle_actions:
		_routes.clear()
		_groups = {"StandGroup": _actions.keys()}
		_safe_state_actions = _filter_safe_state_actions(_safe_state_actions)


func _load_semantic_pose_map() -> void:
	_semantic_map = _load_json_dict(semantic_pose_map_path)
	_semantic_state_actions = _semantic_map.get("state_actions", {})
	_semantic_emotion_actions = _semantic_map.get("emotion_actions", {})
	_semantic_gesture_actions = _semantic_map.get("gesture_actions", {})
	_semantic_posture_defaults = _semantic_map.get("posture_defaults", {})
	_semantic_transition_routes = _semantic_map.get("transition_routes", {})
	_filter_semantic_actions_to_available()

	if _semantic_state_actions.is_empty():
		_semantic_state_actions = {
			"idle": "KA_Idle01_breathing",
			"listening": "KA_Idle02_LookLeftAndRight",
			"thinking": "KA_Idle08_ComeUpWithAnIdea",
			"speaking": "KA_Idle50_StandingTalk1_1",
			"interrupted": "KA_Idle29_Surprised",
			"acting": "KA_Idle45_WaveHandSlightly",
			"sleep": "KA_Idle09_Waiting",
		}
	if _semantic_emotion_actions.is_empty():
		_semantic_emotion_actions = {
			"happy": "KA_Idle28_Laugh",
			"angry": "KA_Idle27_Angry",
			"mocking": "KA_Idle42_Taunt",
			"sleepy": "KA_Idle09_Waiting",
			"surprised": "KA_Idle29_Surprised",
			"confused": "KA_Idle08_ComeUpWithAnIdea",
		}
	if _semantic_gesture_actions.is_empty():
		_semantic_gesture_actions = {
			"small_tease": "KA_Idle42_Taunt",
			"point": "KA_Idle39_CuteArmUp",
			"arms_crossed": "KA_Idle37_Tsundere",
			"nod": "KA_Idle44_GreetingBow",
			"shake_head": "KA_Idle02_LookLeftAndRight",
			"think": "KA_Idle08_ComeUpWithAnIdea",
			"smug": "KA_Idle43_HandOnHip",
		}
	if _semantic_posture_defaults.is_empty():
		_semantic_posture_defaults = {
			"stand": "KA_Idle01_breathing",
			"sit": "KA_Idle10_Sit",
			"lie": "",
			"air": "",
		}
	if _semantic_transition_routes.is_empty():
		_semantic_transition_routes = {
			"air->lie": ["KA_Fly_End_Witch", "KA_Idle01_breathing", "KA_Sleep_Start"],
			"lie->air": ["KA_Sleep_End", "KA_Idle01_breathing", "KA_Fly_Start_Witch"],
			"stand->lie": ["KA_Sleep_Start"],
			"lie->stand": ["KA_Sleep_End", "KA_Idle01_breathing"],
			"stand->air": ["KA_Fly_Start_Witch"],
			"air->stand": ["KA_Fly_End_Witch", "KA_Idle01_breathing"],
			"stand->sit": ["KA_Sit_Start"],
			"sit->stand": ["KA_Sit_End", "KA_Idle01_breathing"],
		}
	_filter_semantic_actions_to_available()
	_load_desktop_action_taxonomy()


func _load_desktop_action_taxonomy() -> void:
	var parsed := _load_json_dict(desktop_action_taxonomy_path)
	if parsed.is_empty():
		return
	var slots: Dictionary = parsed.get("slots", {})
	var bindings: Dictionary = parsed.get("semantic_bindings", {})
	_semantic_state_actions = _apply_taxonomy_slot_bindings(bindings.get("state_to_slot", {}), _semantic_state_actions, slots)
	_semantic_emotion_actions = _apply_taxonomy_slot_bindings(bindings.get("emotion_to_slot", {}), _semantic_emotion_actions, slots)
	_semantic_gesture_actions = _apply_taxonomy_slot_bindings(bindings.get("gesture_to_slot", {}), _semantic_gesture_actions, slots)
	_semantic_posture_defaults = _apply_taxonomy_slot_bindings(bindings.get("posture_to_slot", {}), _semantic_posture_defaults, slots)

	var safe_state_slots: Dictionary = parsed.get("safe_state_slots", {})
	if not safe_state_slots.is_empty():
		_safe_state_actions = _safe_state_actions_from_taxonomy_slots(safe_state_slots, slots)

	var taxonomy_routes := _taxonomy_routes_from_slots(parsed.get("transition_routes", {}), slots)
	if not taxonomy_routes.is_empty():
		_semantic_transition_routes = taxonomy_routes

	_filter_semantic_actions_to_available()


func _apply_taxonomy_slot_bindings(slot_bindings_variant, fallback_actions: Dictionary, slots: Dictionary) -> Dictionary:
	var output := fallback_actions.duplicate()
	if typeof(slot_bindings_variant) != TYPE_DICTIONARY:
		return output
	var slot_bindings: Dictionary = slot_bindings_variant
	for key in slot_bindings.keys():
		var slot_name := String(slot_bindings[key])
		if slot_name.is_empty():
			output[String(key)] = ""
			continue
		var action_names := _resolve_taxonomy_slot_actions(slot_name, slots)
		if action_names.size() == 1:
			output[String(key)] = String(action_names[0])
		elif action_names.size() > 1:
			output[String(key)] = action_names
		elif not output.has(String(key)):
			output[String(key)] = ""
	return output


func _safe_state_actions_from_taxonomy_slots(safe_state_slots: Dictionary, slots: Dictionary) -> Dictionary:
	var output := {}
	for state_name in safe_state_slots.keys():
		var actions := []
		var slot_list: Array = safe_state_slots.get(state_name, [])
		for slot_variant in slot_list:
			for action_variant in _resolve_taxonomy_slot_actions(String(slot_variant), slots):
				var action_name := String(action_variant)
				if not action_name.is_empty() and not actions.has(action_name):
					actions.append(action_name)
		output[String(state_name)] = actions
	return output


func _taxonomy_routes_from_slots(route_bindings_variant, slots: Dictionary) -> Dictionary:
	var output := {}
	if typeof(route_bindings_variant) != TYPE_DICTIONARY:
		return output
	var route_bindings: Dictionary = route_bindings_variant
	for route_key in route_bindings.keys():
		var action_route := []
		var slot_route: Array = route_bindings.get(route_key, [])
		for slot_variant in slot_route:
			var action_name := _resolve_taxonomy_slot_action(String(slot_variant), slots)
			if not action_name.is_empty() and not action_route.has(action_name):
				action_route.append(action_name)
		if not action_route.is_empty():
			output[String(route_key)] = action_route
	return output


func _resolve_taxonomy_slot_action(slot_name: String, slots: Dictionary) -> String:
	var actions := _resolve_taxonomy_slot_actions(slot_name, slots)
	return String(actions[0]) if not actions.is_empty() else ""


func _resolve_taxonomy_slot_actions(slot_name: String, slots: Dictionary) -> Array:
	if slot_name.is_empty() or not slots.has(slot_name):
		return []
	var slot: Dictionary = slots.get(slot_name, {})
	if not bool(slot.get("enabled", true)):
		return []
	var candidates := []
	var primary := String(slot.get("primary", ""))
	if not primary.is_empty():
		candidates.append(primary)
	var pool: Array = slot.get("pool", [])
	for pool_item in pool:
		var action_name := String(pool_item)
		if not action_name.is_empty() and not candidates.has(action_name):
			candidates.append(action_name)
	var output := []
	for action_variant in candidates:
		var action_name := String(action_variant)
		if _actions.has(action_name) and not output.has(action_name):
			output.append(action_name)
	return output


func _resolve_pose_action(command: Dictionary) -> String:
	var gesture := str(command.get("gesture", "none")).to_lower()
	var emotion := str(command.get("emotion", "neutral")).to_lower()
	var state := str(command.get("state", "idle")).to_lower()
	var posture := _sanitize_posture(str(command.get("posture", "stand")))
	var candidates := [
		_semantic_gesture_actions.get(gesture, ""),
		_semantic_emotion_actions.get(emotion, ""),
	]
	if posture != "stand":
		candidates.append(_semantic_posture_defaults.get(posture, ""))
	candidates.append(_semantic_state_actions.get(state, ""))
	candidates.append(_semantic_posture_defaults.get(posture, ""))
	candidates.append(default_action_name)
	for action_variant in candidates:
		var action := _pick_action_from_variant(action_variant)
		if not action.is_empty():
			return action
	return ""


func _filter_semantic_actions_to_available() -> void:
	_semantic_state_actions = _filter_action_dictionary(_semantic_state_actions)
	_semantic_emotion_actions = _filter_action_dictionary(_semantic_emotion_actions)
	_semantic_gesture_actions = _filter_action_dictionary(_semantic_gesture_actions)
	_semantic_posture_defaults = _filter_action_dictionary(_semantic_posture_defaults)
	if only_idle_actions:
		_semantic_transition_routes.clear()


func _filter_action_dictionary(source: Dictionary) -> Dictionary:
	var output := {}
	for key in source.keys():
		var value = source[key]
		if typeof(value) == TYPE_ARRAY:
			var filtered := []
			for action_variant in (value as Array):
				var action_name := String(action_variant)
				if action_name.is_empty() or _actions.has(action_name):
					filtered.append(action_name)
			output[key] = filtered
		else:
			var action_name := String(value)
			output[key] = action_name if action_name.is_empty() or _actions.has(action_name) else ""
	return output


func _pick_action_from_variant(value) -> String:
	if typeof(value) == TYPE_ARRAY:
		var candidates := []
		for action_variant in (value as Array):
			var action_name := String(action_variant)
			if not action_name.is_empty() and _actions.has(action_name) and not candidates.has(action_name):
				candidates.append(action_name)
		if candidates.is_empty():
			return ""
		var preferred := []
		for action_name in candidates:
			if action_name != _current_action and not _recent_pose_actions.has(action_name):
				preferred.append(action_name)
		if preferred.is_empty():
			for action_name in candidates:
				if action_name != _current_action:
					preferred.append(action_name)
		var pool: Array = preferred if not preferred.is_empty() else candidates
		return String(pool[_rng.randi_range(0, pool.size() - 1)])
	var action_name := String(value)
	return action_name if not action_name.is_empty() and _actions.has(action_name) else ""


func _remember_recent_action(action_name: String) -> void:
	if action_name.is_empty():
		return
	_recent_pose_actions.erase(action_name)
	_recent_pose_actions.push_front(action_name)
	while _recent_pose_actions.size() > 4:
		_recent_pose_actions.pop_back()


func _filter_safe_state_actions(source: Dictionary) -> Dictionary:
	var output := {}
	for key in source.keys():
		var filtered_actions := []
		var actions: Array = source.get(key, [])
		for action_variant in actions:
			var action_name := String(action_variant)
			if _actions.has(action_name):
				filtered_actions.append(action_name)
		output[key] = filtered_actions
	return output


func _is_action_allowed(action_name: String) -> bool:
	if not only_idle_actions:
		return true
	var idle_number := _idle_action_number(action_name)
	if idle_number < 0:
		return false
	for blocked_number in excluded_idle_action_numbers:
		if int(blocked_number) == idle_number:
			return false
	return true


func _idle_action_number(action_name: String) -> int:
	if not action_name.begins_with("KA_Idle"):
		return -1
	var digits := ""
	for i in range(7, action_name.length()):
		var character := action_name.substr(i, 1)
		if not character.is_valid_int():
			break
		digits += character
	if digits.is_empty():
		return -1
	return int(digits)


func _sanitize_posture(posture: String) -> String:
	var normalized := posture.to_lower()
	if normalized in ["stand", "sit", "lie", "air"]:
		return normalized
	return "stand"


func _ensure_animation_tree() -> void:
	if not animation_tree_path.is_empty():
		_animation_tree = get_node_or_null(animation_tree_path) as AnimationTree
	if _animation_tree == null and auto_create_animation_tree:
		_animation_tree = AnimationTree.new()
		_animation_tree.name = "PetAnimationTree"
		add_child(_animation_tree)
	if _animation_tree == null:
		return
	_tree_root = AnimationNodeStateMachine.new()
	_add_group_state("StandGroup", Vector2(0, 0))
	_add_group_state("SitGroup", Vector2(220, -120))
	_add_group_state("LieGroup", Vector2(220, 120))
	_add_group_state("AirGroup", Vector2(440, -120))
	_add_group_state("TransitionGroup", Vector2(440, 120))
	_animation_tree.tree_root = _tree_root
	_animation_tree.active = true
	_playback = _animation_tree.get("parameters/playback") as AnimationNodeStateMachinePlayback


func _add_group_state(group_name: String, position: Vector2) -> void:
	if _tree_root == null:
		return
	var node := AnimationNodeAnimation.new()
	node.animation = group_name
	_tree_root.add_node(group_name, node, position)


func _load_json_dict(path: String) -> Dictionary:
	if not FileAccess.file_exists(path):
		push_warning("AnimationDirector config missing: %s" % path)
		return {}
	var file := FileAccess.open(path, FileAccess.READ)
	if file == null:
		push_warning("AnimationDirector could not open config: %s" % path)
		return {}
	var parsed = JSON.parse_string(file.get_as_text())
	if typeof(parsed) != TYPE_DICTIONARY:
		push_warning("AnimationDirector invalid JSON dictionary: %s" % path)
		return {}
	return parsed
