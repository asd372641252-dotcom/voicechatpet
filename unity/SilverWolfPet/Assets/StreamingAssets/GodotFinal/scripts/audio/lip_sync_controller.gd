extends Node

signal audio_started(path: String)
signal audio_finished()
signal audio_failed(path: String, reason: String)

@export var expression_controller_path: NodePath
@export_file("*.json") var expression_map_path := "res://config/expression_map.json"
@export var audio_bus_name := "PetLipSync"
@export var rms_floor := 0.015
@export var rms_ceiling := 0.18
@export var response_speed := 14.0
@export var release_speed := 10.0
@export var max_capture_frames := 2048

var _expression_controller: Node
var _audio_player: AudioStreamPlayer
var _capture_effect: AudioEffectCapture
var _mouth_expression_name := ""
var _last_weight := 0.0


func _ready() -> void:
	_expression_controller = get_node_or_null(expression_controller_path)
	_audio_player = AudioStreamPlayer.new()
	_audio_player.name = "AudioPlayer"
	add_child(_audio_player)
	_audio_player.finished.connect(_on_audio_finished)

	_setup_capture_bus()
	_read_lip_sync_expression()


func _process(delta: float) -> void:
	if _mouth_expression_name.is_empty() or _expression_controller == null:
		return

	var target_weight := _read_rms_weight()
	var speed := response_speed if target_weight > _last_weight else release_speed
	_last_weight = lerpf(_last_weight, target_weight, clampf(delta * speed, 0.0, 1.0))
	_apply_mouth_weight(_last_weight)


func play_audio(path: String) -> bool:
	var normalized_path := path.replace("\\", "/")
	if not ResourceLoader.exists(normalized_path):
		audio_failed.emit(normalized_path, "Audio resource does not exist.")
		push_warning("Audio resource does not exist: %s" % normalized_path)
		return false

	var stream := ResourceLoader.load(normalized_path)
	if stream == null or not (stream is AudioStream):
		audio_failed.emit(normalized_path, "Resource is not an AudioStream.")
		push_warning("Resource is not an AudioStream: %s" % normalized_path)
		return false

	if _capture_effect != null:
		_capture_effect.clear_buffer()
	_audio_player.stream = stream
	_audio_player.bus = audio_bus_name
	_audio_player.play()
	audio_started.emit(normalized_path)
	return true


func stop_lip_sync() -> void:
	if _audio_player != null and _audio_player.playing:
		_audio_player.stop()
	_last_weight = 0.0
	_apply_mouth_weight(0.0)


func _setup_capture_bus() -> void:
	var bus_index := AudioServer.get_bus_index(audio_bus_name)
	if bus_index == -1:
		AudioServer.add_bus(AudioServer.get_bus_count())
		bus_index = AudioServer.get_bus_count() - 1
		AudioServer.set_bus_name(bus_index, audio_bus_name)

	_capture_effect = AudioEffectCapture.new()
	AudioServer.add_bus_effect(bus_index, _capture_effect)


func _read_lip_sync_expression() -> void:
	if _expression_controller != null and _expression_controller.has_method("get_lip_sync_expression_name"):
		_mouth_expression_name = str(_expression_controller.call("get_lip_sync_expression_name"))
		if not _mouth_expression_name.is_empty():
			return

	var data := _load_json(expression_map_path)
	var lip_sync = data.get("lip_sync", {})
	if typeof(lip_sync) == TYPE_DICTIONARY:
		_mouth_expression_name = str(lip_sync.get("expression", ""))


func _read_rms_weight() -> float:
	if _capture_effect == null:
		return 0.0

	var frame_count := _capture_effect.get_frames_available()
	if frame_count <= 0:
		return 0.0

	var frames_to_read: int = min(frame_count, max_capture_frames)
	var frames := _capture_effect.get_buffer(frames_to_read)
	if frames.is_empty():
		return 0.0

	var sum_squares := 0.0
	for frame in frames:
		var sample := (frame.x + frame.y) * 0.5
		sum_squares += sample * sample

	var rms := sqrt(sum_squares / float(frames.size()))
	return clampf((rms - rms_floor) / maxf(rms_ceiling - rms_floor, 0.001), 0.0, 1.0)


func _apply_mouth_weight(weight: float) -> void:
	if _expression_controller != null and _expression_controller.has_method("set_expression_weight"):
		_expression_controller.call("set_expression_weight", _mouth_expression_name, clampf(weight, 0.0, 1.0))


func _on_audio_finished() -> void:
	_last_weight = 0.0
	_apply_mouth_weight(0.0)
	audio_finished.emit()


func _load_json(path: String) -> Dictionary:
	if not FileAccess.file_exists(path):
		return {}

	var file := FileAccess.open(path, FileAccess.READ)
	if file == null:
		return {}

	var parsed = JSON.parse_string(file.get_as_text())
	if typeof(parsed) != TYPE_DICTIONARY:
		return {}
	return parsed

