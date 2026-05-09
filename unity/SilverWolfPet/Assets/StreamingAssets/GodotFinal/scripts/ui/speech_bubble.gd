extends Control

@export var auto_hide_sec := 0.0
@export var min_width := 96.0
@export var max_width := 360.0
@export var min_height := 42.0
@export var max_height := 260.0
@export var fade_sec := 0.14
@export var push_anim_sec := 0.24
@export var message_gap := 8.0
@export var queue_gap_sec := 0.18
@export var min_read_sec := 4.5
@export var read_sec_per_visual_char := 0.11
@export var max_read_sec := 14.0
@export var stream_update_char_limit := 56
@export var max_messages := 3
@export var follow_model := true
@export var draggable := true
@export var persistent_visible := true
@export var placeholder_enabled := true
@export var placeholder_text := "……"
@export var camera_path: NodePath
@export var model_source_path: NodePath
@export var click_through_controller_path: NodePath
@export var world_anchor_offset := Vector3(0.34, 1.62, 0.0)
@export var screen_offset := Vector2(18.0, -92.0)
@export var viewport_margin := 12.0
@export var edge_safe_padding := 24.0

var _tail: Polygon2D
var _fade_overlay: TextureRect
var _hide_timer: Timer
var _chunk_timer: Timer
var _fade_tween: Tween
var _layout_tween: Tween
var _message_records: Array[Dictionary] = []
var _queued_chunks: Array[String] = []
var _last_text := ""
var _next_chunk_time_msec := 0
var _bubble_enabled := true
var _dragging := false
var _drag_offset := Vector2.ZERO
var _ui_built := false


func _ready() -> void:
	mouse_filter = Control.MOUSE_FILTER_STOP if draggable else Control.MOUSE_FILTER_IGNORE
	_ensure_ui_built()
	if persistent_visible and placeholder_enabled:
		_show_placeholder()
	else:
		hide()


func _process(_delta: float) -> void:
	if not visible:
		return
	if follow_model and not _dragging:
		_update_follow_position()
	_sync_clickthrough_rect(true)


func _notification(what: int) -> void:
	if what == NOTIFICATION_VISIBILITY_CHANGED and not visible:
		_sync_clickthrough_rect(false)


func _gui_input(event: InputEvent) -> void:
	if not draggable:
		return
	if event is InputEventMouseButton:
		var mouse_button := event as InputEventMouseButton
		if mouse_button.button_index != MOUSE_BUTTON_LEFT:
			return
		if mouse_button.pressed:
			_dragging = true
			follow_model = false
			_drag_offset = get_global_mouse_position() - global_position
			accept_event()
		else:
			_dragging = false
			accept_event()
	elif event is InputEventMouseMotion and _dragging:
		_set_dragged_global_position(get_global_mouse_position() - _drag_offset)
		accept_event()


func show_text(text: String, duration_sec: float = -1.0) -> void:
	var clean_text := text.strip_edges()
	if not _bubble_enabled:
		_last_text = clean_text
		return
	_show_text_internal(clean_text, duration_sec)


func force_show_text(text: String, duration_sec: float = -1.0) -> void:
	var clean_text := text.strip_edges()
	if clean_text.is_empty():
		return
	_show_text_internal(clean_text, duration_sec)


func _show_text_internal(clean_text: String, duration_sec: float = -1.0) -> void:
	_ensure_ui_built()
	if clean_text.is_empty():
		hide_text(false)
		return

	var chunks := _split_chat_chunks(clean_text)
	if chunks.is_empty():
		chunks = [clean_text]

	_queue_chat_chunks(chunks)

	_last_text = clean_text
	_schedule_next_chunk_if_needed()
	if follow_model:
		_update_follow_position()
	_sync_clickthrough_rect(true)
	_show_with_fade()

	var duration := auto_hide_sec if duration_sec < 0.0 else duration_sec
	if duration > 0.0 and _queued_chunks.is_empty():
		_hide_timer.start(duration)
	else:
		_hide_timer.stop()


func hide_text(show_placeholder := true) -> void:
	_ensure_ui_built()
	_last_text = ""
	if _hide_timer != null:
		_hide_timer.stop()
	if _chunk_timer != null:
		_chunk_timer.stop()
	if _fade_tween != null:
		_fade_tween.kill()
	if _layout_tween != null:
		_layout_tween.kill()
	_queued_chunks.clear()
	_next_chunk_time_msec = 0
	_clear_messages()
	if show_placeholder and persistent_visible and placeholder_enabled and _bubble_enabled:
		_show_placeholder()
		return
	hide()
	_sync_clickthrough_rect(false)


func clear_text_without_placeholder() -> void:
	hide_text(false)


func set_bubble_enabled(enabled: bool) -> void:
	_bubble_enabled = enabled
	if _bubble_enabled:
		if persistent_visible and placeholder_enabled and _message_records.is_empty():
			_show_placeholder()
		elif not _last_text.is_empty():
			show_text(_last_text, 0.0)
		else:
			hide()
	else:
		if _hide_timer != null:
			_hide_timer.stop()
		if _chunk_timer != null:
			_chunk_timer.stop()
		_queued_chunks.clear()
		_next_chunk_time_msec = 0
		if _fade_tween != null:
			_fade_tween.kill()
		hide()
		_sync_clickthrough_rect(false)


func get_bubble_enabled() -> bool:
	return _bubble_enabled


func set_persistent_visible(enabled: bool, next_placeholder_text: String = "") -> void:
	persistent_visible = enabled
	if not next_placeholder_text.is_empty():
		placeholder_text = next_placeholder_text
	if not _bubble_enabled:
		return
	if persistent_visible and placeholder_enabled and _message_records.is_empty():
		_show_placeholder()
	elif _message_records.is_empty():
		hide()
		_sync_clickthrough_rect(false)


func set_placeholder_enabled(enabled: bool, clear_current_placeholder: bool = true) -> void:
	placeholder_enabled = enabled
	if not _bubble_enabled:
		return
	if placeholder_enabled:
		if persistent_visible and _message_records.is_empty():
			_show_placeholder()
		return
	if clear_current_placeholder and _has_only_placeholder_message():
		_clear_messages()
		hide()
		_sync_clickthrough_rect(false)


func set_follow_paths(next_camera_path: NodePath, next_model_source_path: NodePath) -> void:
	camera_path = next_camera_path
	model_source_path = next_model_source_path
	_update_follow_position()


func _show_placeholder() -> void:
	_ensure_ui_built()
	if not placeholder_enabled:
		hide()
		_sync_clickthrough_rect(false)
		return
	_queued_chunks.clear()
	_next_chunk_time_msec = 0
	if _chunk_timer != null:
		_chunk_timer.stop()
	_clear_messages()
	_add_or_update_message(placeholder_text, true)
	if follow_model and not _dragging:
		_update_follow_position()
	_sync_clickthrough_rect(true)
	_show_with_fade()


func _build_ui() -> void:
	anchors_preset = Control.PRESET_TOP_LEFT
	position = Vector2(18.0, 24.0)
	size = Vector2(max_width, 96.0)

	_tail = Polygon2D.new()
	_tail.name = "Tail"
	_tail.color = Color(0.086, 0.094, 0.11, 0.92)
	_tail.z_index = 0
	add_child(_tail)

	_fade_overlay = TextureRect.new()
	_fade_overlay.name = "TopFade"
	_fade_overlay.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_fade_overlay.z_index = 20
	_fade_overlay.visible = false
	_fade_overlay.texture = _make_top_fade_texture()
	add_child(_fade_overlay)

	_hide_timer = Timer.new()
	_hide_timer.one_shot = true
	_hide_timer.timeout.connect(hide_text)
	add_child(_hide_timer)

	_chunk_timer = Timer.new()
	_chunk_timer.one_shot = true
	_chunk_timer.timeout.connect(_show_next_queued_chunk)
	add_child(_chunk_timer)


func _ensure_ui_built() -> void:
	if _ui_built:
		return
	_build_ui()
	_ui_built = true


func _make_top_fade_texture() -> Texture2D:
	var gradient := Gradient.new()
	gradient.offsets = PackedFloat32Array([0.0, 1.0])
	gradient.colors = PackedColorArray([
		Color(0.086, 0.094, 0.11, 0.42),
		Color(0.086, 0.094, 0.11, 0.0),
	])
	var texture := GradientTexture2D.new()
	texture.gradient = gradient
	texture.fill_from = Vector2(0.0, 0.0)
	texture.fill_to = Vector2(0.0, 1.0)
	texture.width = 8
	texture.height = 64
	return texture


func _add_or_update_message(text: String, placeholder := false) -> void:
	var clean_text := text.strip_edges()
	if clean_text.is_empty():
		return

	if placeholder:
		_clear_messages()
	elif _has_only_placeholder_message():
		_clear_messages()

	if not _message_records.is_empty():
		var last_record: Dictionary = _message_records[_message_records.size() - 1]
		var last_text := str(last_record.get("text", ""))
		if clean_text == last_text:
			return
		if clean_text.begins_with(last_text) and clean_text.length() - last_text.length() <= 36:
			_update_message_record(last_record, clean_text)
			_relayout_messages(false)
			return
		if last_text.begins_with(clean_text):
			return

	var record := _create_message_record(clean_text, placeholder)
	_message_records.append(record)
	add_child(record["panel"])
	_prune_old_messages()
	_relayout_messages(true)


func _create_message_record(text: String, placeholder := false) -> Dictionary:
	var panel := PanelContainer.new()
	panel.name = "ChatMessage"
	panel.mouse_filter = Control.MOUSE_FILTER_IGNORE
	panel.z_index = 5

	var label := Label.new()
	label.name = "Text"
	label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	label.text_overrun_behavior = TextServer.OVERRUN_NO_TRIMMING
	label.add_theme_color_override("font_color", Color(0.92, 0.94, 0.98, 1.0))
	label.add_theme_color_override("font_shadow_color", Color(0.0, 0.0, 0.0, 0.35))
	label.add_theme_constant_override("shadow_offset_x", 0)
	label.add_theme_constant_override("shadow_offset_y", 1)
	var font := SystemFont.new()
	font.font_names = PackedStringArray(["Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Arial"])
	label.add_theme_font_override("font", font)
	label.add_theme_font_size_override("font_size", 18)
	panel.add_child(label)

	var record := {
		"panel": panel,
		"label": label,
		"text": text,
		"placeholder": placeholder,
		"size": Vector2.ZERO,
	}
	_update_message_record(record, text)
	panel.modulate.a = 0.0
	return record


func _update_message_record(record: Dictionary, text: String) -> void:
	record["text"] = text
	var label := record["label"] as Label
	label.text = text

	var message_size := _measure_message_size(text)
	record["size"] = message_size
	var panel := record["panel"] as PanelContainer
	panel.custom_minimum_size = message_size
	panel.size = message_size
	label.custom_minimum_size = Vector2(maxf(1.0, message_size.x - 32.0), maxf(1.0, message_size.y - 24.0))


func _measure_message_size(text: String) -> Vector2:
	var visual_len := _visual_text_length(text)
	var usable_max_width := _get_usable_max_width()
	var desired_width := clampf(48.0 + visual_len * 14.5, min_width, usable_max_width)
	var content_width := maxf(64.0, desired_width - 32.0)
	var chars_per_line := maxi(4, int(floor(content_width / 17.0)))
	var line_count := maxi(1, int(ceil(visual_len / float(chars_per_line))))
	var desired_height := clampf(30.0 + float(line_count) * 23.0, min_height, max_height)
	return Vector2(desired_width, desired_height)


func _visual_text_length(text: String) -> float:
	var length := 0.0
	for index in range(text.length()):
		var code := text.unicode_at(index)
		if code <= 0x7F:
			length += 0.56
		else:
			length += 1.0
	return maxf(1.0, length)


func _prune_old_messages() -> void:
	while _message_records.size() > maxi(1, max_messages):
		var old_record: Dictionary = _message_records.pop_front()
		var old_panel := old_record["panel"] as Control
		var tween := create_tween()
		tween.set_parallel(true)
		tween.tween_property(old_panel, "modulate:a", 0.0, push_anim_sec * 0.75)
		tween.tween_property(old_panel, "position:y", old_panel.position.y - 18.0, push_anim_sec * 0.75)
		tween.finished.connect(old_panel.queue_free)


func _relayout_messages(animate_newest: bool) -> void:
	if _layout_tween != null:
		_layout_tween.kill()

	var content_width := 0.0
	var content_height := 0.0
	for record in _message_records:
		var message_size := record["size"] as Vector2
		content_width = maxf(content_width, message_size.x)
		content_height += message_size.y
	if _message_records.size() > 1:
		content_height += message_gap * float(_message_records.size() - 1)

	size = Vector2(maxf(content_width, min_width), maxf(content_height + 22.0, min_height + 22.0))
	_update_tail(content_height)
	_update_top_fade(content_width, content_height)

	var y := 0.0
	_layout_tween = create_tween()
	_layout_tween.set_parallel(true)
	var count: int = _message_records.size()
	for index in range(count):
		var record: Dictionary = _message_records[index]
		var panel := record["panel"] as Control
		var message_size := record["size"] as Vector2
		var target_position := Vector2(0.0, y)
		if animate_newest and index == count - 1:
			panel.position = target_position + Vector2(0.0, 18.0)
		var target_alpha := _message_alpha(index, count)
		_layout_tween.tween_property(panel, "position", target_position, push_anim_sec).set_trans(Tween.TRANS_CUBIC).set_ease(Tween.EASE_OUT)
		_layout_tween.tween_property(panel, "modulate:a", target_alpha, push_anim_sec).set_trans(Tween.TRANS_CUBIC).set_ease(Tween.EASE_OUT)
		_apply_message_style(record, index, count)
		y += message_size.y + message_gap

	if follow_model and not _dragging:
		_update_follow_position()
	else:
		_clamp_current_position_to_viewport()
	_sync_clickthrough_rect(true)


func _message_alpha(index: int, count: int) -> float:
	if count <= 1:
		return 1.0
	if index == 0 and count >= 3:
		return 0.42
	if index == 0:
		return 0.72
	if index == 1 and count >= 3:
		return 0.78
	return 1.0


func _apply_message_style(record: Dictionary, index: int, count: int) -> void:
	var panel := record["panel"] as PanelContainer
	var style := StyleBoxFlat.new()
	var alpha := 0.91
	if count >= 3 and index == 0:
		alpha = 0.46
	elif count >= 2 and index == 0:
		alpha = 0.72
	style.bg_color = Color(0.086, 0.094, 0.11, alpha)
	style.border_color = Color(1.0, 1.0, 1.0, minf(0.14, alpha * 0.14))
	style.set_border_width_all(1)
	style.set_corner_radius_all(16)
	style.content_margin_left = 16.0
	style.content_margin_right = 16.0
	style.content_margin_top = 11.0
	style.content_margin_bottom = 11.0
	style.shadow_color = Color(0.0, 0.0, 0.0, 0.35 * alpha)
	style.shadow_size = 10
	style.shadow_offset = Vector2(0.0, 4.0)
	panel.add_theme_stylebox_override("panel", style)


func _update_tail(content_height: float) -> void:
	if _tail == null:
		return
	var tail_y := maxf(24.0, content_height - 10.0)
	_tail.color = Color(0.086, 0.094, 0.11, 0.92)
	_tail.polygon = PackedVector2Array([
		Vector2(34.0, tail_y),
		Vector2(74.0, tail_y),
		Vector2(48.0, tail_y + 26.0),
	])


func _update_top_fade(content_width: float, content_height: float) -> void:
	if _fade_overlay == null:
		return
	_fade_overlay.visible = _message_records.size() >= 3
	_fade_overlay.position = Vector2.ZERO
	_fade_overlay.size = Vector2(maxf(content_width, min_width), minf(58.0, content_height))


func _show_with_fade() -> void:
	if _fade_tween != null:
		_fade_tween.kill()
	if visible:
		modulate.a = 1.0
		return
	visible = true
	modulate.a = 0.0
	_fade_tween = create_tween()
	_fade_tween.tween_property(self, "modulate:a", 1.0, fade_sec)


func _queue_chat_chunks(chunks: Array[String]) -> void:
	if _has_only_placeholder_message():
		_clear_messages()
	for chunk in chunks:
		_queue_or_update_chunk(chunk)


func _queue_or_update_chunk(text: String) -> void:
	var clean := text.strip_edges()
	if clean.is_empty():
		return
	if _try_update_visible_chunk(clean):
		return
	if _try_update_queued_chunk(clean):
		return
	_queued_chunks.append(clean)


func _try_update_visible_chunk(text: String) -> bool:
	if _message_records.is_empty():
		return false
	for index in range(_message_records.size() - 1, -1, -1):
		var record: Dictionary = _message_records[index]
		if bool(record.get("placeholder", false)):
			continue
		var old_text := str(record.get("text", ""))
		if text == old_text or old_text.begins_with(text):
			return true
		if text.begins_with(old_text) and text.length() - old_text.length() <= stream_update_char_limit:
			_update_message_record(record, text)
			_relayout_messages(false)
			_reserve_read_time(text)
			return true
		break
	return false


func _try_update_queued_chunk(text: String) -> bool:
	for index in range(_queued_chunks.size()):
		var old_text := _queued_chunks[index]
		if text == old_text or old_text.begins_with(text):
			return true
		if text.begins_with(old_text) and text.length() - old_text.length() <= stream_update_char_limit:
			_queued_chunks[index] = text
			return true
	return false


func _show_next_queued_chunk() -> void:
	if _queued_chunks.is_empty():
		return
	var chunk: String = _queued_chunks.pop_front()
	_add_or_update_message(chunk)
	_last_text = chunk
	_reserve_read_time(chunk)
	if follow_model:
		_update_follow_position()
	_sync_clickthrough_rect(true)
	_show_with_fade()
	_schedule_next_chunk_if_needed()


func _schedule_next_chunk_if_needed() -> void:
	if _queued_chunks.is_empty() or _chunk_timer == null:
		return
	if not _chunk_timer.is_stopped():
		return
	var now_msec := Time.get_ticks_msec()
	var wait_msec := maxi(0, _next_chunk_time_msec - now_msec)
	if wait_msec <= 0:
		_show_next_queued_chunk()
	else:
		_chunk_timer.start(float(wait_msec) / 1000.0)


func _reserve_read_time(text: String) -> void:
	_next_chunk_time_msec = Time.get_ticks_msec() + int((_read_time_for_text(text) + queue_gap_sec) * 1000.0)


func _read_time_for_text(text: String) -> float:
	return clampf(min_read_sec + _visual_text_length(text) * read_sec_per_visual_char, min_read_sec, max_read_sec)


func _split_chat_chunks(text: String) -> Array[String]:
	var chunks: Array[String] = []
	var current := ""
	for index in range(text.length()):
		var ch := text.substr(index, 1)
		current += ch
		if "。！？!?".contains(ch):
			_push_chunk(chunks, current)
			current = ""
		elif _visual_text_length(current) >= 42.0 and "，、,；;：:".contains(ch):
			_push_chunk(chunks, current)
			current = ""
	_push_chunk(chunks, current)
	return chunks


func _push_chunk(chunks: Array[String], text: String) -> void:
	var clean := text.strip_edges()
	if clean.is_empty():
		return
	chunks.append(clean)


func _clear_messages() -> void:
	for record in _message_records:
		var panel := record.get("panel") as Node
		if panel != null and is_instance_valid(panel):
			panel.queue_free()
	_message_records.clear()
	if _fade_overlay != null:
		_fade_overlay.visible = false


func _has_only_placeholder_message() -> bool:
	return _message_records.size() == 1 and bool(_message_records[0].get("placeholder", false))


func _update_follow_position() -> void:
	var anchor := _get_anchor_screen_position()
	if anchor == Vector2.INF:
		return

	var bubble_size := _get_bubble_size()
	var next_position := anchor + screen_offset
	next_position = _clamp_position_to_safe_rect(next_position, bubble_size)
	position = next_position


func _get_anchor_screen_position() -> Vector2:
	var camera := get_node_or_null(camera_path) as Camera3D
	var model_source := get_node_or_null(model_source_path)
	if camera == null or model_source == null:
		return Vector2.INF

	var model_root := model_source
	if model_source.has_method("get_model_root"):
		var resolved = model_source.call("get_model_root")
		if resolved is Node:
			model_root = resolved

	if not (model_root is Node3D):
		return Vector2.INF

	var anchor_world := (model_root as Node3D).global_position + world_anchor_offset
	if camera.is_position_behind(anchor_world):
		return Vector2.INF
	return camera.unproject_position(anchor_world)


func _get_bubble_size() -> Vector2:
	return Vector2(maxf(size.x, min_width), maxf(size.y, min_height + 22.0))


func _get_usable_max_width() -> float:
	var safe_rect := _get_safe_local_rect()
	var safe_width := safe_rect.size.x
	if safe_width <= 0.0:
		return max_width
	var available_width := maxf(min_width, safe_width - viewport_margin * 2.0 - edge_safe_padding)
	return minf(max_width, available_width)


func _clamp_current_position_to_viewport() -> void:
	var bubble_size := _get_bubble_size()
	position = _clamp_position_to_safe_rect(position, bubble_size)


func _set_dragged_global_position(next_global_position: Vector2) -> void:
	var bubble_size := _get_bubble_size()
	next_global_position = _clamp_position_to_safe_rect(next_global_position, bubble_size)
	global_position = next_global_position
	_sync_clickthrough_rect(true)


func _clamp_position_to_safe_rect(next_position: Vector2, bubble_size: Vector2) -> Vector2:
	var safe_rect := _get_safe_local_rect()
	var min_x := safe_rect.position.x + viewport_margin
	var min_y := safe_rect.position.y + viewport_margin
	var max_x := safe_rect.position.x + safe_rect.size.x - bubble_size.x - viewport_margin
	var max_y := safe_rect.position.y + safe_rect.size.y - bubble_size.y - viewport_margin
	if max_x < min_x:
		max_x = min_x
	if max_y < min_y:
		max_y = min_y
	return Vector2(
		clampf(next_position.x, min_x, max_x),
		clampf(next_position.y, min_y, max_y)
	)


func _get_safe_local_rect() -> Rect2:
	var viewport_rect := get_viewport_rect()
	var safe_rect := Rect2(Vector2.ZERO, viewport_rect.size)
	if DisplayServer.get_name() != "Windows":
		return safe_rect
	var window_id := get_window().get_window_id()
	var screen_index := DisplayServer.window_get_current_screen(window_id)
	var usable := DisplayServer.screen_get_usable_rect(screen_index)
	var window_position := Vector2(DisplayServer.window_get_position(window_id))
	var desktop_safe := Rect2(Vector2(usable.position) - window_position, Vector2(usable.size))
	var merged := safe_rect.intersection(desktop_safe)
	if merged.size.x <= 0.0 or merged.size.y <= 0.0:
		return safe_rect
	return merged


func _sync_clickthrough_rect(enabled: bool) -> void:
	var controller := get_node_or_null(click_through_controller_path)
	if controller == null:
		return
	var rect := Rect2(global_position, _get_bubble_size())
	if controller.has_method("SetExtraInteractiveRectNamed"):
		controller.call("SetExtraInteractiveRectNamed", "speech_bubble", rect, enabled and visible)
	elif controller.has_method("SetExtraInteractiveRect"):
		controller.call("SetExtraInteractiveRect", rect, enabled and visible)
