extends Node

signal pose_command_received(command: Dictionary)
signal pose_command_rejected(reason: String, payload: Dictionary)
signal client_connected()
signal client_disconnected()

@export var host := "127.0.0.1"
@export var port := 17865
@export var enabled := true
@export var animation_director_path: NodePath = NodePath("../PetAnimationDirector")
@export var expression_controller_path: NodePath = NodePath("../ExpressionController")
@export var lip_sync_controller_path: NodePath = NodePath("../LipSyncController")
@export var speech_bubble_path: NodePath = NodePath("../UI/SpeechBubble")
@export var print_commands := false
@export var mouth_flap_enabled := true
@export var mouth_flap_cycle_sec := 0.18
@export var mouth_flap_closed_weight := 0.0
@export var mouth_flap_open_weight := 0.52
@export var mouth_flap_open_hold_ratio := 0.56
@export var mouth_viseme_min_hold_sec := 0.11
@export var mouth_viseme_max_hold_sec := 0.24
@export var mouth_text_driven_enabled := true
@export var mouth_text_queue_max_chars := 80

const ALLOWED_FIELDS := {
	"type": true,
	"state": true,
	"emotion": true,
	"gesture": true,
	"posture": true,
	"bubble_text": true,
	"mouth": true,
	"mouth_open": true,
	"audio_active": true,
	"face": true,
	"emotion_intensity": true,
	"eye_style": true,
	"overlay_only": true,
	"force_bubble": true,
	"clear_bubble": true,
	"priority": true,
	"duration_ms": true,
	"interruptible": true,
}
const FORBIDDEN_FIELDS := {
	"animation_name": true,
	"bone_name": true,
	"raw_transform": true,
	"file_path": true,
	"script": true,
	"code": true,
}
const ALLOWED_STATES := {"idle": true, "listening": true, "thinking": true, "speaking": true, "interrupted": true, "acting": true, "sleep": true}
const ALLOWED_EMOTIONS := {"neutral": true, "happy": true, "angry": true, "mocking": true, "sleepy": true, "surprised": true, "confused": true}
const ALLOWED_GESTURES := {"none": true, "small_tease": true, "point": true, "arms_crossed": true, "nod": true, "shake_head": true, "think": true, "smug": true}
const ALLOWED_POSTURES := {"stand": true, "sit": true, "lie": true, "air": true}
const ALLOWED_MOUTH := {"none": true, "audio_volume": true, "viseme": true}
const EMOTION_TO_EXPRESSION := {
	"neutral": "neutral",
	"happy": "happy",
	"angry": "angry",
	"mocking": "happy",
	"sleepy": "sleeping",
	"surprised": "happy",
	"confused": "thinking",
}
const FACE_TO_EXPRESSION := {
	"neutral": "neutral",
	"focused": "thinking",
	"thinking": "thinking",
	"bored": "sleeping",
	"sleepy": "sleeping",
	"amused": "happy",
	"smug": "happy",
	"mocking": "happy",
	"mocking_light": "happy",
	"mocking_heavy": "happy",
	"annoyed": "angry",
	"impatient": "angry",
	"angry": "angry",
	"confused": "thinking",
	"surprised": "happy",
	"serious": "thinking",
	"proud": "happy",
	"comforting": "neutral",
	"victory": "happy",
	"fail_tease": "happy",
	"happy": "happy",
}
const STATE_TO_EXPRESSION := {
	"idle": "neutral",
	"listening": "neutral",
	"thinking": "thinking",
	"speaking": "talk",
	"interrupted": "happy",
	"acting": "neutral",
	"sleep": "sleeping",
}
const MOUTH_VISEME_SEQUENCE := [
	{"expression": "mouth_small", "weight": 0.92},
	{"expression": "mouth_wide", "weight": 0.72},
	{"expression": "mouth_small", "weight": 0.62},
	{"expression": "mouth_round", "weight": 0.68},
	{"expression": "mouth_closed", "weight": 0.45},
	{"expression": "mouth_smirk", "weight": 0.52},
]

var _server := TCPServer.new()
var _clients: Array[StreamPeerTCP] = []
var _buffers: Dictionary = {}
var _animation_director: Node
var _expression_controller: Node
var _lip_sync_controller: Node
var _speech_bubble: Node
var _mouth_flap_active := false
var _mouth_flap_elapsed := 0.0
var _mouth_expression_name := ""
var _mouth_next_viseme_sec := 0.0
var _mouth_viseme_index := -1
var _mouth_rng := RandomNumberGenerator.new()
var _mouth_text_queue := ""
var _mouth_last_subtitle_text := ""
var _mouth_volume_scale := 1.0
var _voice_activity_seen := false


func _ready() -> void:
	_mouth_rng.randomize()
	_animation_director = get_node_or_null(animation_director_path)
	_expression_controller = get_node_or_null(expression_controller_path)
	_lip_sync_controller = get_node_or_null(lip_sync_controller_path)
	_speech_bubble = get_node_or_null(speech_bubble_path)
	if enabled:
		_start_server()


func _exit_tree() -> void:
	_server.stop()
	_clients.clear()
	_buffers.clear()


func _process(_delta: float) -> void:
	_process_mouth_flap(_delta)
	if not enabled:
		return
	_accept_clients()
	_poll_clients()


func _start_server() -> void:
	var error := _server.listen(port, host)
	if error != OK:
		push_warning("PetPoseServer failed to listen on %s:%d error=%d" % [host, port, error])
		return
	print("PetPoseServer listening on %s:%d" % [host, port])


func _accept_clients() -> void:
	while _server.is_connection_available():
		var client := _server.take_connection()
		if client == null:
			return
		_clients.append(client)
		_buffers[client] = ""
		client_connected.emit()


func _poll_clients() -> void:
	for i in range(_clients.size() - 1, -1, -1):
		var client := _clients[i]
		var status := client.get_status()
		if status == StreamPeerTCP.STATUS_NONE or status == StreamPeerTCP.STATUS_ERROR:
			_remove_client_at(i)
			continue
		var available := client.get_available_bytes()
		if available <= 0:
			continue
		var result := client.get_data(available)
		if result[0] != OK:
			_remove_client_at(i)
			continue
		var text := (result[1] as PackedByteArray).get_string_from_utf8()
		_consume_text(client, text)


func _consume_text(client: StreamPeerTCP, text: String) -> void:
	var buffer := str(_buffers.get(client, "")) + text
	while buffer.find("\n") >= 0:
		var split_at := buffer.find("\n")
		var line := buffer.substr(0, split_at).strip_edges()
		buffer = buffer.substr(split_at + 1)
		if not line.is_empty():
			_handle_json_text(line)
	_buffers[client] = buffer

	if not buffer.is_empty():
		var parsed = JSON.parse_string(buffer)
		if typeof(parsed) == TYPE_DICTIONARY:
			_buffers[client] = ""
			_handle_command(parsed)


func _handle_json_text(text: String) -> void:
	var parsed = JSON.parse_string(text)
	if typeof(parsed) != TYPE_DICTIONARY:
		_reject("Invalid JSON object", {})
		return
	_handle_command(parsed)


func _handle_command(raw_command: Dictionary) -> void:
	var command := _normalize_command(raw_command)
	if command.is_empty():
		return

	pose_command_received.emit(command)
	_apply_bubble(command)
	_apply_expression(command)
	_apply_mouth(command)
	_apply_pose(command)

	if print_commands and not _is_mouth_only_packet(command):
		print("PetPoseServer command=%s" % JSON.stringify(command))


func _normalize_command(raw_command: Dictionary) -> Dictionary:
	for key in raw_command.keys():
		var field := str(key)
		if FORBIDDEN_FIELDS.has(field):
			_reject("Forbidden pose command field: %s" % field, raw_command)
			return {}
		if not ALLOWED_FIELDS.has(field):
			_reject("Unknown pose command field: %s" % field, raw_command)
			return {}

	var command := {
		"type": str(raw_command.get("type", "pet_pose")),
		"state": str(raw_command.get("state", "idle")).to_lower(),
		"emotion": str(raw_command.get("emotion", "neutral")).to_lower(),
		"gesture": str(raw_command.get("gesture", "none")).to_lower(),
		"posture": str(raw_command.get("posture", "stand")).to_lower(),
		"bubble_text": str(raw_command.get("bubble_text", "")),
		"mouth": str(raw_command.get("mouth", "none")).to_lower(),
		"mouth_open": clampf(float(raw_command.get("mouth_open", -1.0)), -1.0, 1.0),
		"audio_active": bool(raw_command.get("audio_active", false)),
		"_audio_active_present": raw_command.has("audio_active"),
		"face": str(raw_command.get("face", "")).to_lower(),
		"emotion_intensity": clampf(float(raw_command.get("emotion_intensity", 0.0)), 0.0, 1.0),
		"eye_style": str(raw_command.get("eye_style", "")).to_lower(),
		"overlay_only": bool(raw_command.get("overlay_only", false)),
		"force_bubble": bool(raw_command.get("force_bubble", false)),
		"clear_bubble": bool(raw_command.get("clear_bubble", false)),
		"priority": int(raw_command.get("priority", 0)),
		"duration_ms": max(0, int(raw_command.get("duration_ms", 0))),
		"interruptible": bool(raw_command.get("interruptible", true)),
	}

	if command["type"] != "pet_pose":
		_reject("Unsupported command type: %s" % command["type"], raw_command)
		return {}
	if not ALLOWED_STATES.has(command["state"]):
		_reject("Invalid state: %s" % command["state"], raw_command)
		return {}
	if not ALLOWED_EMOTIONS.has(command["emotion"]):
		_reject("Invalid emotion: %s" % command["emotion"], raw_command)
		return {}
	if not ALLOWED_GESTURES.has(command["gesture"]):
		_reject("Invalid gesture: %s" % command["gesture"], raw_command)
		return {}
	if not ALLOWED_POSTURES.has(command["posture"]):
		_reject("Invalid posture: %s" % command["posture"], raw_command)
		return {}
	if not ALLOWED_MOUTH.has(command["mouth"]):
		_reject("Invalid mouth mode: %s" % command["mouth"], raw_command)
		return {}

	return command


func _apply_bubble(command: Dictionary) -> void:
	if _speech_bubble == null:
		return
	_ensure_voice_placeholder_disabled(command)
	if bool(command.get("clear_bubble", false)):
		if _speech_bubble.has_method("clear_text_without_placeholder"):
			_speech_bubble.call("clear_text_without_placeholder")
		elif _speech_bubble.has_method("hide_text"):
			_speech_bubble.call("hide_text", false)
		return
	var text := str(command.get("bubble_text", ""))
	if text.is_empty():
		if _should_clear_stale_bubble(command):
			if _speech_bubble.has_method("clear_text_without_placeholder"):
				_speech_bubble.call("clear_text_without_placeholder")
			elif _speech_bubble.has_method("hide_text"):
				_speech_bubble.call("hide_text", false)
		return
	if bool(command.get("force_bubble", false)):
		if _speech_bubble.has_method("set_placeholder_enabled"):
			_speech_bubble.call("set_placeholder_enabled", false, true)
		if _speech_bubble.has_method("force_show_text"):
			var forced_duration := float(command.get("duration_ms", 0)) / 1000.0
			_speech_bubble.call("force_show_text", text, forced_duration if forced_duration > 0.0 else 0.0)
			return
	if _speech_bubble.has_method("show_text"):
		var duration := float(command.get("duration_ms", 0)) / 1000.0
		_speech_bubble.call("show_text", text, duration if duration > 0.0 else 0.0)


func _apply_expression(command: Dictionary) -> void:
	if _expression_controller == null or not _expression_controller.has_method("set_expression"):
		return
	if _is_mouth_only_packet(command):
		return
	var face := str(command.get("face", "")).to_lower()
	var emotion := str(command.get("emotion", "neutral")).to_lower()
	var expression := str(FACE_TO_EXPRESSION.get(face, "")) if not face.is_empty() else ""
	if expression.is_empty():
		expression = "" if emotion == "neutral" else str(EMOTION_TO_EXPRESSION.get(emotion, ""))
	if expression.is_empty():
		expression = str(STATE_TO_EXPRESSION.get(command.get("state", "idle"), "neutral"))
	var intensity := clampf(float(command.get("emotion_intensity", 1.0)), 0.0, 1.0)
	var weight := 1.0 if intensity <= 0.0 else clampf(0.35 + intensity * 0.65, 0.0, 1.0)
	_expression_controller.call("set_expression", expression, weight)


func _apply_mouth(command: Dictionary) -> void:
	var mouth := str(command.get("mouth", "none"))
	var state := str(command.get("state", "idle"))
	if state == "speaking":
		_queue_mouth_text(str(command.get("bubble_text", "")))
	if mouth == "audio_volume":
		_update_mouth_volume_scale(command)
		if bool(command.get("audio_active", false)):
			_start_mouth_flap()
		elif bool(command.get("_audio_active_present", false)):
			_stop_mouth_flap()
		return
	if state != "speaking":
		_stop_mouth_flap()
		return
	if mouth == "none" and bool(command.get("_audio_active_present", false)) and not bool(command.get("audio_active", false)):
		_stop_mouth_flap()
	if _lip_sync_controller == null:
		return
	if mouth == "none" and _lip_sync_controller.has_method("stop_lip_sync"):
		_lip_sync_controller.call("stop_lip_sync")


func _apply_pose(command: Dictionary) -> void:
	if bool(command.get("overlay_only", false)):
		return
	if _animation_director != null and _animation_director.has_method("request_pose"):
		_animation_director.call("request_pose", command)


func _is_mouth_only_packet(command: Dictionary) -> bool:
	return (
		bool(command.get("overlay_only", false))
		and (str(command.get("mouth", "none")) == "audio_volume" or bool(command.get("_audio_active_present", false)))
		and str(command.get("bubble_text", "")).is_empty()
		and str(command.get("face", "")).is_empty()
		and str(command.get("emotion", "neutral")) == "neutral"
		and str(command.get("gesture", "none")) == "none"
		and float(command.get("emotion_intensity", 0.0)) <= 0.0
	)


func _should_clear_stale_bubble(command: Dictionary) -> bool:
	return bool(command.get("clear_bubble", false))


func _ensure_voice_placeholder_disabled(command: Dictionary) -> void:
	if _voice_activity_seen:
		return
	var state := str(command.get("state", "")).to_lower()
	if not ["listening", "thinking", "speaking", "interrupted"].has(state):
		return
	_voice_activity_seen = true
	if _speech_bubble.has_method("set_placeholder_enabled"):
		_speech_bubble.call("set_placeholder_enabled", false, true)
	if _speech_bubble.has_method("clear_text_without_placeholder"):
		_speech_bubble.call("clear_text_without_placeholder")
	elif _speech_bubble.has_method("hide_text"):
		_speech_bubble.call("hide_text", false)


func _start_mouth_flap() -> void:
	if not mouth_flap_enabled:
		return
	if _expression_controller == null:
		return
	if not _mouth_flap_active:
		_mouth_flap_elapsed = 0.0
		_mouth_next_viseme_sec = 0.0
		_mouth_viseme_index = -1
		_mouth_flap_active = true
		_advance_mouth_viseme()
		return
	_mouth_flap_active = true


func _stop_mouth_flap() -> void:
	if not _mouth_flap_active:
		_apply_mouth_expression("mouth_closed", 0.0)
		return
	_mouth_flap_active = false
	_mouth_flap_elapsed = 0.0
	_mouth_next_viseme_sec = 0.0
	_mouth_viseme_index = -1
	_mouth_text_queue = ""
	_mouth_last_subtitle_text = ""
	_mouth_volume_scale = 1.0
	_apply_mouth_expression("mouth_closed", 0.0)


func _process_mouth_flap(delta: float) -> void:
	if not _mouth_flap_active or _expression_controller == null:
		return
	_ensure_mouth_expression_name()
	if _mouth_expression_name.is_empty():
		pass
	_mouth_flap_elapsed += delta
	if _mouth_flap_elapsed >= _mouth_next_viseme_sec:
		_advance_mouth_viseme()


func _advance_mouth_viseme() -> void:
	if _expression_controller == null:
		return
	if mouth_text_driven_enabled and not _mouth_text_queue.is_empty():
		var text_viseme := _take_next_text_viseme()
		if not text_viseme.is_empty():
			_apply_mouth_viseme(text_viseme)
			return

	var viseme_count: int = MOUTH_VISEME_SEQUENCE.size()
	if viseme_count <= 0:
		_apply_mouth_weight(mouth_flap_open_weight)
		return

	var step := 1
	if _mouth_rng.randf() < 0.28:
		step = 2
	_mouth_viseme_index = posmod(_mouth_viseme_index + step, viseme_count)
	var viseme: Dictionary = MOUTH_VISEME_SEQUENCE[_mouth_viseme_index]
	_apply_mouth_viseme(viseme)


func _apply_mouth_viseme(viseme: Dictionary) -> void:
	var expression_name := str(viseme.get("expression", "mouth_small"))
	var viseme_weight := clampf(float(viseme.get("weight", 1.0)) * mouth_flap_open_weight * _mouth_volume_scale, 0.0, 1.0)
	_apply_mouth_expression(expression_name, viseme_weight)
	_mouth_flap_elapsed = 0.0
	var hold_sec := float(viseme.get("hold_sec", -1.0))
	if hold_sec > 0.0:
		_mouth_next_viseme_sec = hold_sec
	else:
		var min_hold: float = maxf(mouth_viseme_min_hold_sec, 0.06)
		var max_hold: float = maxf(mouth_viseme_max_hold_sec, min_hold)
		_mouth_next_viseme_sec = _mouth_rng.randf_range(min_hold, max_hold)


func _queue_mouth_text(text: String) -> void:
	if not mouth_text_driven_enabled:
		return
	var incoming := text.strip_edges()
	if incoming.is_empty():
		return

	var addition := incoming
	if not _mouth_last_subtitle_text.is_empty():
		if incoming == _mouth_last_subtitle_text:
			return
		if incoming.begins_with(_mouth_last_subtitle_text):
			addition = incoming.substr(_mouth_last_subtitle_text.length())
		elif _mouth_last_subtitle_text.begins_with(incoming):
			return

	_mouth_last_subtitle_text = incoming
	addition = _sanitize_mouth_text(addition)
	if addition.is_empty():
		return
	_mouth_text_queue += addition
	var max_chars: int = maxi(8, mouth_text_queue_max_chars)
	if _mouth_text_queue.length() > max_chars:
		_mouth_text_queue = _mouth_text_queue.substr(_mouth_text_queue.length() - max_chars)


func _sanitize_mouth_text(text: String) -> String:
	var result := ""
	for index in range(text.length()):
		var ch := text.substr(index, 1)
		if ch == "\n" or ch == "\r" or ch == "\t":
			result += " "
		else:
			result += ch
	return result


func _take_next_text_viseme() -> Dictionary:
	while not _mouth_text_queue.is_empty():
		var ch := _mouth_text_queue.substr(0, 1)
		_mouth_text_queue = _mouth_text_queue.substr(1)
		var viseme := _mouth_viseme_for_char(ch)
		if not viseme.is_empty():
			return viseme
	return {}


func _mouth_viseme_for_char(ch: String) -> Dictionary:
	if ch.is_empty() or ch == " ":
		return {"expression": "mouth_closed", "weight": 0.0, "hold_sec": 0.12}
	if _is_sentence_pause(ch):
		return {"expression": "mouth_closed", "weight": 0.0, "hold_sec": _mouth_rng.randf_range(0.22, 0.42)}
	if _is_short_pause(ch):
		return {"expression": "mouth_closed", "weight": 0.1, "hold_sec": _mouth_rng.randf_range(0.13, 0.24)}

	var lower := ch.to_lower()
	if "a啊哈呀啦まばぱさざたなはらわあかが".contains(lower):
		return {"expression": "mouth_open", "weight": _mouth_rng.randf_range(0.72, 0.95), "hold_sec": _mouth_rng.randf_range(0.08, 0.15)}
	if "o哦喔噢我过说多おこごそぞとどのほぼぽもよろを".contains(lower):
		return {"expression": "mouth_round", "weight": _mouth_rng.randf_range(0.68, 0.92), "hold_sec": _mouth_rng.randf_range(0.08, 0.16)}
	if "u呜唔不出住主うくぐすずつづぬふぶぷむゆる".contains(lower):
		return {"expression": "mouth_round", "weight": _mouth_rng.randf_range(0.5, 0.76), "hold_sec": _mouth_rng.randf_range(0.07, 0.14)}
	if "i你一细轻いきぎしじちぢにひびぴみり".contains(lower):
		return {"expression": "mouth_wide", "weight": _mouth_rng.randf_range(0.48, 0.72), "hold_sec": _mouth_rng.randf_range(0.06, 0.13)}
	if "e欸诶也这的了えけげせぜてでねへべぺめれ".contains(lower):
		return {"expression": "mouth_small", "weight": _mouth_rng.randf_range(0.46, 0.72), "hold_sec": _mouth_rng.randf_range(0.07, 0.14)}
	if "嗯唔ん".contains(lower):
		return {"expression": "mouth_closed", "weight": _mouth_rng.randf_range(0.2, 0.45), "hold_sec": _mouth_rng.randf_range(0.08, 0.16)}

	var code := ch.unicode_at(0)
	if _is_cjk_code(code):
		match code % 6:
			0:
				return {"expression": "mouth_small", "weight": _mouth_rng.randf_range(0.48, 0.72), "hold_sec": _mouth_rng.randf_range(0.07, 0.14)}
			1:
				return {"expression": "mouth_wide", "weight": _mouth_rng.randf_range(0.42, 0.66), "hold_sec": _mouth_rng.randf_range(0.07, 0.14)}
			2:
				return {"expression": "mouth_round", "weight": _mouth_rng.randf_range(0.42, 0.68), "hold_sec": _mouth_rng.randf_range(0.08, 0.15)}
			3:
				return {"expression": "mouth_open", "weight": _mouth_rng.randf_range(0.52, 0.78), "hold_sec": _mouth_rng.randf_range(0.08, 0.15)}
			4:
				return {"expression": "mouth_smirk", "weight": _mouth_rng.randf_range(0.35, 0.58), "hold_sec": _mouth_rng.randf_range(0.08, 0.15)}
			_:
				return {"expression": "mouth_closed", "weight": _mouth_rng.randf_range(0.08, 0.25), "hold_sec": _mouth_rng.randf_range(0.05, 0.1)}

	match code % 5:
		0:
			return {"expression": "mouth_small", "weight": 0.6}
		1:
			return {"expression": "mouth_wide", "weight": 0.55}
		2:
			return {"expression": "mouth_round", "weight": 0.56}
		3:
			return {"expression": "mouth_open", "weight": 0.62}
		_:
			return {"expression": "mouth_closed", "weight": 0.18}


func _is_sentence_pause(ch: String) -> bool:
	return "。！？!?…".contains(ch)


func _is_short_pause(ch: String) -> bool:
	return "，、,；;：:".contains(ch)


func _is_cjk_code(code: int) -> bool:
	return (
		(code >= 0x3400 and code <= 0x9FFF)
		or (code >= 0x3040 and code <= 0x30FF)
		or (code >= 0xAC00 and code <= 0xD7AF)
	)


func _update_mouth_volume_scale(command: Dictionary) -> void:
	var mouth_open := float(command.get("mouth_open", -1.0))
	if mouth_open < 0.0:
		return
	_mouth_volume_scale = clampf(0.7 + clampf(mouth_open, 0.0, 1.0) * 0.45, 0.65, 1.15)


func _apply_mouth_expression(expression_name: String, weight: float) -> void:
	if _expression_controller == null:
		return
	if _expression_controller.has_method("set_expression"):
		_expression_controller.call("set_expression", expression_name, clampf(weight, 0.0, 1.0))
		return
	if expression_name == _mouth_expression_name:
		_apply_mouth_weight(weight)


func _ensure_mouth_expression_name() -> void:
	if not _mouth_expression_name.is_empty():
		return
	if _expression_controller != null and _expression_controller.has_method("get_lip_sync_expression_name"):
		_mouth_expression_name = str(_expression_controller.call("get_lip_sync_expression_name"))


func _apply_mouth_weight(weight: float) -> void:
	if _expression_controller == null or _mouth_expression_name.is_empty():
		return
	if _expression_controller.has_method("set_expression_weight"):
		_expression_controller.call("set_expression_weight", _mouth_expression_name, clampf(weight, 0.0, 1.0))


func _reject(reason: String, payload: Dictionary) -> void:
	pose_command_rejected.emit(reason, payload)
	push_warning("PetPoseServer rejected command: %s" % reason)


func _remove_client_at(index: int) -> void:
	var client := _clients[index]
	_buffers.erase(client)
	_clients.remove_at(index)
	client_disconnected.emit()
