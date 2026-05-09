extends Node

@export var state_machine_path: NodePath
@export var expression_controller_path: NodePath
@export var pose_controller_path: NodePath
@export var lip_sync_controller_path: NodePath
@export var speech_bubble_path: NodePath
@export var talking_state := "talking"

var _state_machine: Node
var _expression_controller: Node
var _pose_controller: Node
var _lip_sync_controller: Node
var _speech_bubble: Node


func _ready() -> void:
	_state_machine = get_node_or_null(state_machine_path)
	_expression_controller = get_node_or_null(expression_controller_path)
	_pose_controller = get_node_or_null(pose_controller_path)
	_lip_sync_controller = get_node_or_null(lip_sync_controller_path)
	_speech_bubble = get_node_or_null(speech_bubble_path)

	if _state_machine != null and _state_machine.has_signal("state_changed"):
		_state_machine.state_changed.connect(_on_state_changed)
		_sync_current_state()

	var event_bus := get_node_or_null("/root/PetEventBus")
	if event_bus != null:
		event_bus.set_state_requested.connect(_on_set_state_requested)
		event_bus.say_text_requested.connect(_on_say_text_requested)
		event_bus.play_audio_requested.connect(_on_play_audio_requested)
		event_bus.set_expression_requested.connect(_on_set_expression_requested)


func set_state(state_name: String, payload: Dictionary = {}) -> bool:
	if _state_machine == null or not _state_machine.has_method("set_state"):
		return false
	return bool(_state_machine.call("set_state", state_name, payload))


func say_text(text: String, payload: Dictionary = {}) -> void:
	var state_name := str(payload.get("state", talking_state))
	set_state(state_name, payload)
	_show_bubble(text, float(payload.get("duration_sec", -1.0)))


func play_audio(audio_path: String, payload: Dictionary = {}) -> bool:
	var state_name := str(payload.get("state", talking_state))
	set_state(state_name, payload)

	if payload.has("text"):
		_show_bubble(str(payload["text"]), float(payload.get("duration_sec", -1.0)))

	if _lip_sync_controller != null and _lip_sync_controller.has_method("play_audio"):
		return bool(_lip_sync_controller.call("play_audio", audio_path))
	return false


func set_expression(expression_name: String, weight: float = 1.0, payload: Dictionary = {}) -> bool:
	if _expression_controller != null and _expression_controller.has_method("set_expression"):
		return bool(_expression_controller.call("set_expression", expression_name, weight))
	return false


func _sync_current_state() -> void:
	if _state_machine == null:
		return
	if not _state_machine.has_method("get_current_state") or not _state_machine.has_method("get_state_data"):
		return

	var state_name := str(_state_machine.call("get_current_state"))
	if state_name.is_empty():
		return
	var state_data: Variant = _state_machine.call("get_state_data", state_name)
	if typeof(state_data) == TYPE_DICTIONARY:
		_on_state_changed(state_name, state_data, {})


func _on_state_changed(state_name: String, state_data: Dictionary, payload: Dictionary) -> void:
	var expression_name := str(state_data.get("expression", ""))
	if not expression_name.is_empty():
		set_expression(expression_name, float(payload.get("expression_weight", 1.0)), payload)

	var motion_name := str(state_data.get("motion", ""))
	if not motion_name.is_empty() and _pose_controller != null and _pose_controller.has_method("apply_motion"):
		_pose_controller.call("apply_motion", motion_name, {"state": state_name})

	var bubble_text := str(payload.get("text", state_data.get("bubble_text", "")))
	if bubble_text.is_empty():
		_hide_bubble()
	else:
		_show_bubble(bubble_text, float(payload.get("duration_sec", -1.0)))

	if not bool(state_data.get("audio_lip_sync", false)) and _lip_sync_controller != null:
		if _lip_sync_controller.has_method("stop_lip_sync"):
			_lip_sync_controller.call("stop_lip_sync")


func _on_set_state_requested(state_name: String, payload: Dictionary) -> void:
	set_state(state_name, payload)


func _on_say_text_requested(text: String, payload: Dictionary) -> void:
	say_text(text, payload)


func _on_play_audio_requested(audio_path: String, payload: Dictionary) -> void:
	play_audio(audio_path, payload)


func _on_set_expression_requested(expression_name: String, weight: float, payload: Dictionary) -> void:
	set_expression(expression_name, weight, payload)


func _show_bubble(text: String, duration_sec: float = -1.0) -> void:
	if _speech_bubble != null and _speech_bubble.has_method("show_text"):
		_speech_bubble.call("show_text", text, duration_sec)


func _hide_bubble() -> void:
	if _speech_bubble != null and _speech_bubble.has_method("hide_text"):
		_speech_bubble.call("hide_text")
