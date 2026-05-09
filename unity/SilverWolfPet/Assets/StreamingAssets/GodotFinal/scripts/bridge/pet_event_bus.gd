extends Node

signal set_state_requested(state_name: String, payload: Dictionary)
signal say_text_requested(text: String, payload: Dictionary)
signal play_audio_requested(audio_path: String, payload: Dictionary)
signal set_expression_requested(expression_name: String, weight: float, payload: Dictionary)


func set_state(state_name: String, payload: Dictionary = {}) -> void:
	set_state_requested.emit(state_name, payload)


func say_text(text: String, payload: Dictionary = {}) -> void:
	say_text_requested.emit(text, payload)


func play_audio(audio_path: String, payload: Dictionary = {}) -> void:
	play_audio_requested.emit(audio_path, payload)


func set_expression(expression_name: String, weight: float = 1.0, payload: Dictionary = {}) -> void:
	set_expression_requested.emit(expression_name, weight, payload)


func dispatch(event_type: String, payload: Dictionary = {}) -> void:
	match event_type:
		"set_state":
			set_state(str(payload.get("state", "")), payload)
		"say_text":
			say_text(str(payload.get("text", "")), payload)
		"play_audio":
			play_audio(str(payload.get("path", payload.get("audio_path", ""))), payload)
		"set_expression":
			set_expression(str(payload.get("expression", "")), float(payload.get("weight", 1.0)), payload)
		_:
			push_warning("Unknown pet bridge event: %s" % event_type)

