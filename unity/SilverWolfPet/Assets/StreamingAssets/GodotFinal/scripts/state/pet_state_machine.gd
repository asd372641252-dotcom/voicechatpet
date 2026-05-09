extends Node

signal state_changed(state_name: String, state_data: Dictionary, payload: Dictionary)
signal state_rejected(state_name: String, reason: String)

@export_file("*.json") var states_path := "res://config/pet_states.json"
@export var initial_state := "idle"

var _states: Dictionary = {}
var _current_state := ""
var _current_payload: Dictionary = {}


func _ready() -> void:
	_load_states()
	var configured_initial := str(_states.get("_initial_state", initial_state))
	set_state(configured_initial)


func set_state(state_name: String, payload: Dictionary = {}) -> bool:
	if not _states.has(state_name):
		state_rejected.emit(state_name, "Unknown state.")
		push_warning("Unknown pet state: %s" % state_name)
		return false

	_current_state = state_name
	_current_payload = payload.duplicate(true)
	state_changed.emit(state_name, get_state_data(state_name), _current_payload)
	return true


func get_current_state() -> String:
	return _current_state


func get_state_data(state_name: String = "") -> Dictionary:
	var target_state := state_name
	if target_state.is_empty():
		target_state = _current_state
	if not _states.has(target_state):
		return {}
	return (_states[target_state] as Dictionary).duplicate(true)


func has_state(state_name: String) -> bool:
	return _states.has(state_name)


func _load_states() -> void:
	_states.clear()
	var data := _load_json(states_path)
	if data.is_empty():
		return

	if data.has("initial_state"):
		_states["_initial_state"] = str(data["initial_state"])

	var state_table = data.get("states", {})
	if typeof(state_table) != TYPE_DICTIONARY:
		push_warning("pet_states.json has no states dictionary.")
		return

	for state_name in state_table.keys():
		var state_data = state_table[state_name]
		if typeof(state_data) == TYPE_DICTIONARY:
			_states[str(state_name)] = state_data


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
