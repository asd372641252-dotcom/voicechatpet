extends Node3D

signal action_loaded(action_name: String)
signal action_load_failed(reason: String)
signal model_loaded(model_root: Node)
signal voice_chat_requested(route_id: String)
signal voice_chat_stop_requested()
signal app_quit_requested()
signal screen_vision_start_requested(route_id: String)
signal screen_vision_stop_requested()
signal companion_polling_interval_requested(seconds: int)

@export_file("*.glb", "*.vrm") var model_path := "res://assets/converted/user_pet_model.glb"
@export_file("*.json") var pet_config_path := "res://config/pet_config.json"
@export_file("*.json") var action_bundle_path := "res://assets/action_import/kawaii_wave_action_bundle.json"
@export_dir var action_bundle_dir := "res://assets/action_import/kawaii100"
@export var load_action_list_on_ready := true
@export var model_parent_path: NodePath = NodePath("ModelSlot")
@export var status_label_path: NodePath = NodePath("HUD/Panel/StatusLabel")
@export var status_panel_path: NodePath = NodePath("HUD/Panel")
@export var speech_bubble_path: NodePath = NodePath("HUD/SpeechBubble")
@export var show_status_hud := true
@export var show_speech_bubble := true
@export var speech_bubble_placeholder := "……"
@export_file("*.json") var voice_routes_config_path := "res://config/voice_routes.json"
@export_file("*.json") var traditional_llm_config_path := "res://config/volc_traditional_voice_chat.local.json"
@export var voice_route_id := ""
@export var companion_polling_interval_sec := 10
@export var camera_path: NodePath = NodePath("Camera3D")
@export var material_toggle_controller_path: NodePath = NodePath("AnimeRenderController")
@export var anime_render_controller_path: NodePath = NodePath("AnimeRenderController")
@export var lighting_controller_path: NodePath = NodePath("LightingController")
@export var expression_controller_path: NodePath = NodePath("ExpressionController")
@export var animation_director_path: NodePath = NodePath("PetAnimationDirector")
@export var menu_use_native_window := true
@export var enable_window_click_through := false
@export var left_drag_window := true
@export var ctrl_wheel_resize := true
@export var window_resize_step := 72
@export var min_window_size := Vector2i(256, 360)
@export var max_window_size := Vector2i(3072, 4320)
@export var right_click_menu_threshold := 8.0
@export var menu_toggle_debounce_ms := 220
@export var main_menu_size := Vector2(360.0, 470.0)
@export var action_menu_size := Vector2(460.0, 600.0)
@export var render_debug_menu_size := Vector2(440.0, 620.0)
@export var action_menu_font_size := 18
@export var opaque_test_background := false
@export var auto_play := true
@export var loop := true
@export var playback_speed := 1.0
@export var action_transition_sec := 0.42
@export var runtime_resample_actions := true
@export var target_action_sample_rate := 60.0
@export var auto_expression_from_action := true
@export var auto_return_to_idle := true
@export var return_to_idle_delay_sec := 3.0
@export var return_to_idle_action_name := "KA_Idle01_breathing"
@export var random_idle_enabled := true
@export var random_idle_gap_sec := 16.0
@export var only_idle_actions := true
@export var excluded_idle_action_numbers := PackedInt32Array([7, 13, 30, 33, 34, 57, 58])
@export var startup_idle_action_names := PackedStringArray([
	"KA_Idle01_breathing",
])
@export var random_idle_action_names := PackedStringArray([
	"KA_Idle02_LookLeftAndRight",
	"KA_Idle03_LookAtHands",
	"KA_Idle04_LookAtFeet",
	"KA_Idle05_Stretch",
	"KA_Idle08_ComeUpWithAnIdea",
	"KA_Idle09_Waiting",
	"KA_Idle11_LookingBack",
	"KA_Idle12_LeaningForward",
	"KA_Idle15_TieShoelaces",
	"KA_Idle43_HandOnHip",
])
@export var transformation_action_name := "KA_Idle35_FingerSnap"
@export var transformation_reveal_delay_sec := 0.45
@export var transformation_hide_before_reveal := true
@export_enum("current", "raw", "handed_z", "handed_y") var rotation_conversion := "current"
@export var apply_root_translation := true
@export var auto_fit_to_view := true
@export var target_height := 1.72
@export var target_center_y := 0.88
@export var target_center_z := 0.0
@export var print_summary := true

# ---- menu color palette (dark theme) ----
const MENU_BG := Color(0.086, 0.094, 0.11, 0.965)
const MENU_BORDER := Color(1.0, 1.0, 1.0, 0.12)
const MENU_HEADER_BG := Color(1.0, 1.0, 1.0, 0.04)
const MENU_ACCENT := Color(0.4, 0.65, 1.0, 1.0)
const MENU_TEXT := Color(0.92, 0.94, 0.98, 1.0)
const MENU_TEXT_DIM := Color(0.55, 0.58, 0.64, 1.0)
const MENU_TEXT_MUTED := Color(0.45, 0.48, 0.54, 1.0)
const MENU_BTN_HOVER := Color(1.0, 1.0, 1.0, 0.07)
const MENU_BTN_PRESSED := Color(1.0, 1.0, 1.0, 0.03)
const MENU_DIVIDER := Color(1.0, 1.0, 1.0, 0.06)
const MENU_CLOSE_HOVER := Color(0.95, 0.3, 0.25, 0.85)

const MENU_RESIZE_EDGE := 10.0
const MENU_DRAG_HEADER_HEIGHT := 56.0
const MENU_VIEWPORT_MARGIN := 8.0
const ACTION_MENU_CATEGORY_ORDER := [
	"全部",
	"基础待机",
	"语音状态",
	"情绪反应",
	"工作状态",
	"用户交互",
	"桌宠姿态",
	"姿态过渡",
	"特殊展示",
	"其他",
]

var _model_root: Node3D
var _skeleton: Skeleton3D
var _bone_lookup: Dictionary = {}
var _bone_alias_lookup: Dictionary = {}
var _runtime_rest_pose_map: Dictionary = {}
var _imported_rest_pose_map: Dictionary = {}
var _action_bones: Array[Dictionary] = []
var _action_name := ""
var _transition_start_pose: Dictionary = {}
var _transition_elapsed := 0.0
var _transition_duration := 0.0
var _transition_active := false
var _sample_rate := 30.0
var _source_sample_rate := 30.0
var _frame_count := 0
var _length_sec := 0.0
var _elapsed_sec := 0.0
var _action_wall_elapsed_sec := 0.0
var _playing := false
var _idle_return_armed := false
var _status_label: Label
var _status_panel: Control
var _speech_bubble: Node
var _status_hud_button: Button
var _speech_bubble_button: Button
var _idle_return_button: Button
var _screen_vision_button: Button
var _companion_polling_button: Button
var _companion_interval_buttons: Dictionary = {}
var _voice_start_button: Button
var _voice_route_s2s_button: Button
var _voice_route_s2s_companion_button: Button
var _voice_route_traditional_button: Button
var _voice_route_agent_button: Button
var _voice_routes: Dictionary = {}
var _last_status := ""
var _transformation_sequence := 0
var _voice_chat_active := false
var _screen_vision_active := false
var _rng := RandomNumberGenerator.new()
var _action_entries: Array[Dictionary] = []
var _action_index := -1
var _right_pressed := false
var _right_press_position := Vector2.ZERO
var _menu_layer: CanvasLayer
var _menu_window: Window
var _menu_window_root: Control
var _main_menu_panel: PanelContainer
var _accessory_menu_panel: PanelContainer
var _voice_menu_panel: PanelContainer
var _settings_menu_panel: PanelContainer
var _render_debug_menu_panel: PanelContainer
var _render_preset_option: OptionButton
var _render_light_mode_option: OptionButton
var _api_settings_menu_panel: PanelContainer
var _api_provider_option: OptionButton
var _api_url_edit: LineEdit
var _api_key_edit: LineEdit
var _api_model_edit: LineEdit
var _api_thinking_option: OptionButton
var _api_test_button: Button
var _api_test_request: HTTPRequest
var _api_test_started_msec := 0
var _api_test_url := ""
var _api_test_model := ""
var _action_category_menu_panel: PanelContainer
var _action_menu_panel: PanelContainer
var _action_menu_title: Label
var _action_menu_search: LineEdit
var _action_menu_category: OptionButton
var _action_menu_list: ItemList
var _action_menu_count: Label
var _category_names: PackedStringArray = PackedStringArray()
var _pending_action_menu_category := ""
var _last_menu_position := Vector2(12.0, 12.0)
var _last_menu_screen_position := Vector2(80.0, 80.0)
var _menu_drag_panel: Control
var _menu_drag_offset := Vector2.ZERO
var _menu_resize_panel: Control
var _menu_resize_edges := Vector4.ZERO
var _menu_resize_start_mouse := Vector2.ZERO
var _menu_resize_start_position := Vector2.ZERO
var _menu_resize_start_size := Vector2.ZERO
var _last_click_region_viewport_size := Vector2.ZERO
var _last_click_region_status_visible := false
var _last_menu_open_msec := 0
var _last_menu_close_msec := 0


func _ready() -> void:
	_rng.randomize()
	_apply_pet_model_config()
	_load_voice_routes_config()
	_apply_test_window_background()
	_status_label = get_node_or_null(status_label_path) as Label
	_status_panel = get_node_or_null(status_panel_path) as Control
	_speech_bubble = get_node_or_null(speech_bubble_path)
	_apply_status_hud_visibility()
	_apply_speech_bubble_visibility()
	if not _load_model():
		return
	model_loaded.emit(_model_root)
	if not _bind_runtime_skeleton():
		return
	if load_action_list_on_ready:
		_load_action_entries()
		_select_initial_action_path()
		_select_initial_startup_idle_path()
	if not _load_action_bundle():
		return
	_reset_runtime_pose()
	_elapsed_sec = 0.0
	_playing = auto_play
	action_loaded.emit(_action_name)
	_apply_expression_for_action(_action_name)
	_arm_idle_return_if_needed()
	_sync_animation_director_current_action()
	_update_status()


func _apply_pet_model_config() -> void:
	if pet_config_path.is_empty() or not FileAccess.file_exists(pet_config_path):
		return
	var config := _load_json_dict(pet_config_path)
	var model_config = config.get("model", {})
	if typeof(model_config) != TYPE_DICTIONARY:
		return
	var configured_path := str(model_config.get("path", "")).strip_edges()
	if not configured_path.is_empty():
		model_path = configured_path
	auto_fit_to_view = bool(model_config.get("auto_fit_to_view", auto_fit_to_view))
	target_height = float(model_config.get("target_height", target_height))
	target_center_y = float(model_config.get("target_center_y", target_center_y))
	target_center_z = float(model_config.get("target_center_z", target_center_z))


func _process(delta: float) -> void:
	_process_menu_drag()
	if _skeleton == null or _action_bones.is_empty():
		return
	var scaled_delta := maxf(playback_speed, 0.0) * delta
	_action_wall_elapsed_sec += scaled_delta
	if _playing:
		if _transition_active:
			_transition_elapsed = minf(_transition_elapsed + delta, _transition_duration)
		_elapsed_sec += scaled_delta
		if _should_loop_current_action() and _length_sec > 0.0:
			_elapsed_sec = fmod(_elapsed_sec, _length_sec)
		else:
			_elapsed_sec = minf(_elapsed_sec, _length_sec)
			if _elapsed_sec >= _length_sec:
				_playing = false
		_apply_action_at_time(_elapsed_sec)
		if _transition_active and _transition_elapsed >= _transition_duration:
			_transition_active = false
			_transition_start_pose.clear()
	_process_idle_return()
	_process_random_idle()
	_update_status()


func _input(event: InputEvent) -> void:
	if not menu_use_native_window and _handle_menu_window_input(event):
		get_viewport().set_input_as_handled()
		return

	if event is InputEventMouseButton:
		var mouse_button := event as InputEventMouseButton
		if _handle_wheel_input(mouse_button):
			get_viewport().set_input_as_handled()
			return

		if mouse_button.button_index == MOUSE_BUTTON_LEFT:
			if _uses_native_window_drag():
				return
			if mouse_button.pressed and _should_start_window_drag(mouse_button.position):
				_start_main_window_drag()
				get_viewport().set_input_as_handled()
				return
			return

		if mouse_button.button_index != MOUSE_BUTTON_RIGHT:
			return

		if mouse_button.pressed:
			_right_pressed = true
			_right_press_position = mouse_button.position
			return

		if _right_pressed:
			_right_pressed = false
			if _right_press_position.distance_to(mouse_button.position) <= right_click_menu_threshold:
				_toggle_main_menu(mouse_button.position)
				get_viewport().set_input_as_handled()


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventKey and event.pressed and not event.echo:
		match event.keycode:
			KEY_SPACE:
				_playing = not _playing
				_update_status(true)
			KEY_R:
				_elapsed_sec = 0.0
				_transition_active = false
				_transition_start_pose.clear()
				_reset_runtime_pose()
				_apply_action_at_time(0.0)
				_update_status(true)
			KEY_RIGHT:
				next_action()
			KEY_LEFT:
				previous_action()
			KEY_ESCAPE:
				_hide_all_menus()


func get_model_root() -> Node:
	return _model_root


func _apply_test_window_background() -> void:
	var window := get_window()
	if opaque_test_background:
		window.transparent = false
		window.transparent_bg = false
		get_tree().root.transparent_bg = false
		get_viewport().transparent_bg = false
		DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_TRANSPARENT, false)
		RenderingServer.set_default_clear_color(Color(0.06, 0.06, 0.07, 1.0))
		return
	window.transparent = true
	window.transparent_bg = true
	get_viewport().transparent_bg = true
	get_tree().root.transparent_bg = true
	if RenderingServer.has_method("viewport_set_transparent_background"):
		RenderingServer.call("viewport_set_transparent_background", get_viewport().get_viewport_rid(), true)
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_TRANSPARENT, true)
	RenderingServer.set_default_clear_color(Color(0.0, 0.0, 0.0, 0.0))


func _apply_window_click_through_region(force := false) -> void:
	# Windows click-through is owned by WinClickThroughController.cs.
	# Keep the old Godot polygon path disabled so it cannot override Win32 state.
	pass


func _clear_window_click_through_region() -> void:
	pass


func _build_model_interaction_polygon(viewport_size: Vector2) -> PackedVector2Array:
	var w := maxf(viewport_size.x, 1.0)
	var h := maxf(viewport_size.y, 1.0)
	return PackedVector2Array([
		Vector2(w * 0.48, h * 0.06),
		Vector2(w * 0.66, h * 0.12),
		Vector2(w * 0.92, h * 0.30),
		Vector2(w * 0.96, h * 0.56),
		Vector2(w * 0.78, h * 0.96),
		Vector2(w * 0.32, h * 0.96),
		Vector2(w * 0.13, h * 0.72),
		Vector2(w * 0.16, h * 0.30),
	])


func _should_start_window_drag(position: Vector2) -> bool:
	if not left_drag_window:
		return false
	if _menu_window != null and _menu_window.visible:
		return false
	return Geometry2D.is_point_in_polygon(position, _build_model_interaction_polygon(get_viewport().get_visible_rect().size))


func _uses_native_window_drag() -> bool:
	var controller := get_node_or_null("WinClickThroughController")
	return controller != null and controller.has_method("SetClickThrough")


func _start_main_window_drag() -> void:
	if DisplayServer.has_method("window_start_drag"):
		DisplayServer.call("window_start_drag")
		return
	var window := get_window()
	if window.has_method("start_drag"):
		window.call("start_drag")


func _handle_wheel_input(mouse_button: InputEventMouseButton) -> bool:
	if not mouse_button.pressed:
		return false
	if mouse_button.button_index != MOUSE_BUTTON_WHEEL_UP and mouse_button.button_index != MOUSE_BUTTON_WHEEL_DOWN:
		return false
	var direction := 1 if mouse_button.button_index == MOUSE_BUTTON_WHEEL_UP else -1
	if _has_ctrl_modifier(mouse_button):
		if not ctrl_wheel_resize:
			return false
		_resize_main_window_by_steps(direction)
		return true
	_zoom_camera_by_steps(direction)
	return true


func _zoom_camera_by_steps(direction: int) -> void:
	var camera := get_node_or_null(camera_path)
	if camera == null:
		return
	if camera.has_method("zoom_steps"):
		camera.call("zoom_steps", direction)
		return
	if camera.has_method("_zoom"):
		var zoom_step := float(camera.get("zoom_step")) if "zoom_step" in camera else 0.28
		camera.call("_zoom", -zoom_step * float(direction))


func _resize_main_window_by_steps(direction: int) -> void:
	var current_size := DisplayServer.window_get_size()
	var aspect := float(current_size.x) / maxf(float(current_size.y), 1.0)
	var new_height := clampi(
		current_size.y + direction * window_resize_step,
		min_window_size.y,
		max_window_size.y
	)
	var new_width := clampi(
		int(round(float(new_height) * aspect)),
		min_window_size.x,
		max_window_size.x
	)
	var new_size := Vector2i(new_width, new_height)
	var current_position := DisplayServer.window_get_position()
	var centered_position := current_position + (current_size - new_size) / 2
	DisplayServer.window_set_size(new_size)
	DisplayServer.window_set_position(centered_position)
	_last_click_region_viewport_size = Vector2.ZERO


func _has_ctrl_modifier(event: InputEvent) -> bool:
	if event is InputEventWithModifiers:
		var modified_event := event as InputEventWithModifiers
		return modified_event.ctrl_pressed or Input.is_key_pressed(KEY_CTRL)
	return Input.is_key_pressed(KEY_CTRL)


func _set_window_mouse_passthrough_polygon(region: PackedVector2Array) -> void:
	pass


func next_action() -> bool:
	if _action_entries.is_empty():
		return false
	return _request_action_by_index(_action_index + 1)


func previous_action() -> bool:
	if _action_entries.is_empty():
		return false
	return _request_action_by_index(_action_index - 1)


func _show_main_menu(position: Vector2) -> void:
	_ensure_main_menu()
	if menu_use_native_window:
		_last_menu_screen_position = Vector2(DisplayServer.mouse_get_position())
	_show_menu_panel(_main_menu_panel, position)


func show_context_menu_from_native(position: Vector2) -> void:
	_toggle_main_menu(position)


func _toggle_main_menu(position: Vector2) -> void:
	var now_msec := Time.get_ticks_msec()
	if _has_visible_menu():
		if now_msec - _last_menu_open_msec < menu_toggle_debounce_ms:
			return
		_hide_all_menus()
		return
	if now_msec - _last_menu_close_msec < menu_toggle_debounce_ms:
		return
	_show_main_menu(position)


func _has_visible_menu() -> bool:
	for panel in [_main_menu_panel, _accessory_menu_panel, _voice_menu_panel, _settings_menu_panel, _render_debug_menu_panel, _api_settings_menu_panel, _action_category_menu_panel, _action_menu_panel]:
		if panel != null and panel.visible:
			return true
	return _menu_window != null and _menu_window.visible


func _show_accessory_menu() -> void:
	_ensure_accessory_menu()
	_update_accessory_menu()
	_show_menu_panel(_accessory_menu_panel, _last_menu_position)


func _show_voice_menu() -> void:
	_ensure_voice_menu()
	_show_menu_panel(_voice_menu_panel, _last_menu_position)


func _show_api_settings_menu() -> void:
	_ensure_api_settings_menu()
	_load_api_settings_into_menu()
	_show_menu_panel(_api_settings_menu_panel, _last_menu_position)


func _show_settings_menu() -> void:
	_ensure_settings_menu()
	_ensure_status_hud_toggle_in_settings_menu()
	_update_speech_bubble_button()
	_show_menu_panel(_settings_menu_panel, _last_menu_position)


func _show_render_debug_menu() -> void:
	_ensure_render_debug_menu()
	_sync_render_debug_controls()
	_show_menu_panel(_render_debug_menu_panel, _last_menu_position)


func _show_action_category_menu() -> void:
	_ensure_action_category_menu()
	_show_menu_panel(_action_category_menu_panel, _last_menu_position)


func _show_action_menu(position: Vector2) -> void:
	_ensure_action_menu_cn()
	if _action_menu_search != null and not _pending_action_menu_category.is_empty():
		_action_menu_search.text = ""
	_populate_action_menu_cn()
	if not _pending_action_menu_category.is_empty():
		_set_action_menu_category_cn(_pending_action_menu_category)
		_pending_action_menu_category = ""
		_populate_action_menu_cn()
	_show_menu_panel(_action_menu_panel, position)
	if _action_menu_search != null:
		_action_menu_search.grab_focus()


func _show_action_menu_category(category: String) -> void:
	_pending_action_menu_category = category
	_show_action_menu(_last_menu_position)


func _show_action_category_menu_from_last_position() -> void:
	_ensure_action_category_menu()
	_show_menu_panel(_action_category_menu_panel, _last_menu_position)


func _hide_action_menu() -> void:
	if _action_menu_panel != null:
		_action_menu_panel.hide()


func _hide_all_menus(record_close := true) -> void:
	var had_visible := _has_visible_menu()
	for panel in [_main_menu_panel, _accessory_menu_panel, _voice_menu_panel, _settings_menu_panel, _render_debug_menu_panel, _api_settings_menu_panel, _action_category_menu_panel, _action_menu_panel]:
		if panel != null:
			panel.hide()
	if _menu_window != null:
		_menu_window.hide()
	_menu_drag_panel = null
	_menu_resize_panel = null
	if record_close and had_visible:
		_last_menu_close_msec = Time.get_ticks_msec()


func _show_menu_panel(panel: Control, viewport_position: Vector2) -> void:
	if panel == null:
		return
	_register_menu_drag_surfaces_recursive(panel)
	_hide_all_menus(false)
	panel.show()
	panel.move_to_front()
	_last_menu_open_msec = Time.get_ticks_msec()
	if menu_use_native_window:
		_ensure_menu_layer()
		panel.position = Vector2.ZERO
		_menu_window.size = Vector2i(ceili(panel.size.x), ceili(panel.size.y))
		_menu_window.position = Vector2i(roundi(_last_menu_screen_position.x), roundi(_last_menu_screen_position.y))
		_menu_window.show()
		_menu_window.grab_focus()
		return
	_last_menu_position = _clamp_menu_position(viewport_position, panel.size)
	panel.position = _last_menu_position


func _handle_menu_window_input(event: InputEvent) -> bool:
	if event is InputEventMouseButton:
		var mouse_button := event as InputEventMouseButton
		if mouse_button.button_index != MOUSE_BUTTON_LEFT:
			return false

		if mouse_button.pressed:
			var panel := _top_visible_menu_panel_at(mouse_button.position)
			if panel == null:
				return false
			var local_position := mouse_button.position - panel.global_position
			var edges := _get_menu_resize_edges(panel, local_position)
			if _has_menu_resize_edge(edges):
				_menu_resize_panel = panel
				_menu_resize_edges = edges
				_menu_resize_start_mouse = mouse_button.position
				_menu_resize_start_position = panel.position
				_menu_resize_start_size = panel.size
				panel.move_to_front()
				return true
			if not _menu_click_hits_interactive_control(panel, mouse_button.position):
				_menu_drag_panel = panel
				_menu_drag_offset = mouse_button.position - panel.position
				panel.move_to_front()
				return true
			return false

		var was_dragging := _menu_drag_panel != null or _menu_resize_panel != null
		_menu_drag_panel = null
		_menu_resize_panel = null
		return was_dragging

	if event is InputEventMouseMotion:
		var motion := event as InputEventMouseMotion
		if _menu_drag_panel != null:
			_menu_drag_panel.position = _clamp_menu_position(motion.position - _menu_drag_offset, _menu_drag_panel.size)
			_last_menu_position = _menu_drag_panel.position
			return true
		if _menu_resize_panel != null:
			_resize_menu_panel(motion.position)
			return true

	return false


func _handle_menu_surface_gui_input(panel: Control, event: InputEvent) -> void:
	if not menu_use_native_window or panel == null or not panel.visible:
		return
	if event is InputEventMouseButton:
		var mouse_button := event as InputEventMouseButton
		if mouse_button.button_index == MOUSE_BUTTON_RIGHT:
			if not mouse_button.pressed:
				_hide_all_menus()
				panel.accept_event()
			return
		if mouse_button.button_index != MOUSE_BUTTON_LEFT:
			return
		var screen_position := Vector2(DisplayServer.mouse_get_position())
		var local_position := screen_position - Vector2(_menu_window.position) - panel.position
		if mouse_button.pressed:
			var edges := _get_menu_resize_edges(panel, local_position)
			if _has_menu_resize_edge(edges):
				_start_menu_resize(panel, edges, screen_position)
				panel.accept_event()
				return
			if not _menu_click_hits_interactive_control(panel, screen_position - Vector2(_menu_window.position)):
				_start_menu_drag(panel, screen_position)
				panel.accept_event()
				return
		else:
			_menu_drag_panel = null
			_menu_resize_panel = null
			panel.accept_event()
			return

	if event is InputEventMouseMotion:
		if _menu_drag_panel != null or _menu_resize_panel != null:
			panel.accept_event()


func _start_menu_drag(panel: Control, mouse_position: Vector2) -> void:
	_menu_drag_panel = panel
	if menu_use_native_window:
		_menu_drag_offset = mouse_position - Vector2(_menu_window.position)
	else:
		_menu_drag_offset = mouse_position - panel.position
	panel.move_to_front()


func _start_menu_resize(panel: Control, edges: Vector4, mouse_position: Vector2) -> void:
	_menu_resize_panel = panel
	_menu_resize_edges = edges
	_menu_resize_start_mouse = mouse_position
	if menu_use_native_window:
		_menu_resize_start_position = Vector2(_menu_window.position)
		_menu_resize_start_size = Vector2(_menu_window.size)
	else:
		_menu_resize_start_position = panel.position
		_menu_resize_start_size = panel.size
	panel.move_to_front()


func _process_menu_drag() -> void:
	if _menu_drag_panel == null and _menu_resize_panel == null:
		return
	if not Input.is_mouse_button_pressed(MOUSE_BUTTON_LEFT):
		_menu_drag_panel = null
		_menu_resize_panel = null
		return
	var mouse_position := Vector2(DisplayServer.mouse_get_position()) if menu_use_native_window else get_viewport().get_mouse_position()
	if _menu_drag_panel != null:
		if menu_use_native_window:
			var next_position := mouse_position - _menu_drag_offset
			_menu_window.position = Vector2i(roundi(next_position.x), roundi(next_position.y))
			_last_menu_screen_position = next_position
		else:
			_menu_drag_panel.position = _clamp_menu_position(mouse_position - _menu_drag_offset, _menu_drag_panel.size)
			_last_menu_position = _menu_drag_panel.position
	if _menu_resize_panel != null:
		_resize_menu_panel(mouse_position)


func _register_menu_drag_surfaces_recursive(panel: Control) -> void:
	_register_menu_drag_surface(panel, panel)
	_register_menu_drag_surfaces_under(panel, panel)


func _register_menu_drag_surfaces_under(root: Control, panel: Control) -> void:
	for child in root.get_children():
		if not (child is Control):
			continue
		var control := child as Control
		if _is_menu_interactive_control(control):
			continue
		control.mouse_filter = Control.MOUSE_FILTER_PASS
		_register_menu_drag_surface(control, panel)
		_register_menu_drag_surfaces_under(control, panel)


func _register_menu_drag_surface(control: Control, panel: Control) -> void:
	if control.has_meta("_menu_drag_surface_registered"):
		return
	control.set_meta("_menu_drag_surface_registered", true)
	control.gui_input.connect(func(event: InputEvent) -> void:
		_handle_menu_surface_gui_input(panel, event)
	)


func _top_visible_menu_panel_at(position: Vector2) -> Control:
	var panels: Array[Control] = [
		_main_menu_panel,
		_accessory_menu_panel,
		_voice_menu_panel,
		_settings_menu_panel,
		_render_debug_menu_panel,
		_api_settings_menu_panel,
		_action_category_menu_panel,
		_action_menu_panel,
	]
	for i in range(panels.size() - 1, -1, -1):
		var panel := panels[i]
		if panel == null or not panel.visible:
			continue
		var rect := Rect2(panel.global_position, panel.size)
		if rect.has_point(position):
			return panel
	return null


func _menu_click_hits_interactive_control(root: Control, position: Vector2) -> bool:
	return _find_interactive_control_at(root, position) != null


func _find_interactive_control_at(node: Node, position: Vector2) -> Control:
	for i in range(node.get_child_count() - 1, -1, -1):
		var child := node.get_child(i)
		if not (child is Control):
			continue
		var control := child as Control
		if not control.visible:
			continue
		var rect := Rect2(control.global_position, control.size)
		if not rect.has_point(position):
			continue
		var nested := _find_interactive_control_at(control, position)
		if nested != null:
			return nested
		if _is_menu_interactive_control(control):
			return control
	return null


func _is_menu_interactive_control(control: Control) -> bool:
	return (
		control is BaseButton
		or control is LineEdit
		or control is TextEdit
		or control is ItemList
		or control is OptionButton
		or control is Range
	)


func _get_menu_resize_edges(panel: Control, local_position: Vector2) -> Vector4:
	if local_position.x < 0.0 or local_position.y < 0.0 or local_position.x > panel.size.x or local_position.y > panel.size.y:
		return Vector4.ZERO
	var edges := Vector4.ZERO
	if local_position.x <= MENU_RESIZE_EDGE:
		edges.x = 1.0
	if local_position.y <= MENU_RESIZE_EDGE:
		edges.y = 1.0
	if local_position.x >= panel.size.x - MENU_RESIZE_EDGE:
		edges.z = 1.0
	if local_position.y >= panel.size.y - MENU_RESIZE_EDGE:
		edges.w = 1.0
	return edges


func _has_menu_resize_edge(edges: Vector4) -> bool:
	return edges.x > 0.0 or edges.y > 0.0 or edges.z > 0.0 or edges.w > 0.0


func _resize_menu_panel(mouse_position: Vector2) -> void:
	if _menu_resize_panel == null:
		return
	var delta := mouse_position - _menu_resize_start_mouse
	var next_position := _menu_resize_start_position
	var next_size := _menu_resize_start_size
	var min_size := _minimum_menu_size(_menu_resize_panel)

	if _menu_resize_edges.x > 0.0:
		next_position.x = _menu_resize_start_position.x + delta.x
		next_size.x = _menu_resize_start_size.x - delta.x
		if next_size.x < min_size.x:
			next_position.x = _menu_resize_start_position.x + _menu_resize_start_size.x - min_size.x
			next_size.x = min_size.x
	elif _menu_resize_edges.z > 0.0:
		next_size.x = maxf(min_size.x, _menu_resize_start_size.x + delta.x)

	if _menu_resize_edges.y > 0.0:
		next_position.y = _menu_resize_start_position.y + delta.y
		next_size.y = _menu_resize_start_size.y - delta.y
		if next_size.y < min_size.y:
			next_position.y = _menu_resize_start_position.y + _menu_resize_start_size.y - min_size.y
			next_size.y = min_size.y
	elif _menu_resize_edges.w > 0.0:
		next_size.y = maxf(min_size.y, _menu_resize_start_size.y + delta.y)

	_menu_resize_panel.custom_minimum_size = next_size
	_menu_resize_panel.size = next_size
	if menu_use_native_window:
		_menu_window.size = Vector2i(roundi(next_size.x), roundi(next_size.y))
		_menu_window.position = Vector2i(roundi(next_position.x), roundi(next_position.y))
		_menu_resize_panel.position = Vector2.ZERO
		_last_menu_screen_position = next_position
	else:
		_menu_resize_panel.position = _clamp_menu_position(next_position, next_size)
		_last_menu_position = _menu_resize_panel.position
	_refresh_menu_after_resize(_menu_resize_panel)


func _minimum_menu_size(panel: Control) -> Vector2:
	if panel == _action_menu_panel:
		return Vector2(320.0, 260.0)
	if panel == _render_debug_menu_panel:
		return Vector2(340.0, 320.0)
	if panel == _api_settings_menu_panel:
		return Vector2(360.0, 360.0)
	if panel == _settings_menu_panel:
		return Vector2(300.0, 260.0)
	return Vector2(280.0, 220.0)


func _refresh_menu_after_resize(panel: Control) -> void:
	if panel == _action_menu_panel and _action_menu_list != null:
		_action_menu_list.custom_minimum_size = Vector2(maxf(260.0, panel.size.x - 28.0), maxf(120.0, panel.size.y - 230.0))


func _ensure_menu_layer() -> void:
	if menu_use_native_window:
		_ensure_menu_window()
		return
	if _menu_layer != null:
		return
	_menu_layer = CanvasLayer.new()
	_menu_layer.name = "KawaiiDesktopMenuLayer"
	_menu_layer.layer = 120
	add_child(_menu_layer)


func _ensure_menu_window() -> void:
	if _menu_window != null:
		return
	get_tree().root.gui_embed_subwindows = false
	_menu_window = Window.new()
	_menu_window.name = "KawaiiDesktopMenuWindow"
	_menu_window.title = "桌宠菜单"
	_menu_window.visible = false
	_menu_window.borderless = true
	_menu_window.always_on_top = true
	_menu_window.transparent = true
	_menu_window.transparent_bg = true
	_menu_window.size = Vector2i(int(main_menu_size.x), int(main_menu_size.y))
	_menu_window.min_size = Vector2i(260, 220)
	add_child(_menu_window)

	_menu_window_root = Control.new()
	_menu_window_root.name = "MenuRoot"
	_menu_window_root.set_anchors_preset(Control.PRESET_FULL_RECT)
	_menu_window_root.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_menu_window.add_child(_menu_window_root)


func _ensure_main_menu() -> void:
	if _main_menu_panel != null:
		return
	_ensure_menu_layer()
	_main_menu_panel = _make_menu_panel("KawaiiMainMenu", main_menu_size, Color(1.0, 0.50, 0.78, 1.0))
	var rows := _make_menu_rows(_main_menu_panel)
	_add_menu_header(rows, "主菜单", "桌宠控制面板", _hide_all_menus, "关闭")
	_add_menu_button(rows, "变身演出", "播放觉醒动作，光翼和眼镜一起展开", _play_transformation_sequence, "TransformButton", Color(0.78, 0.62, 1.0, 1.0))
	_add_menu_button(rows, "配饰开关", "眼部眼镜、翅膀和外观开关", _show_accessory_menu, "", Color(0.95, 0.54, 0.82, 1.0))
	_add_menu_button(rows, "浏览动作", "按用途分类选择动作", _show_action_category_menu, "", Color(0.52, 0.86, 1.0, 1.0))
	_add_menu_button(rows, "语音控制", "选择路线、开始对话、陪玩视觉和停止对话", _show_voice_menu, "", Color(0.74, 0.58, 1.0, 1.0))
	_add_menu_button(rows, "API 服务商", "改传统视觉 LLM 的接口、Key 和模型", _show_api_settings_menu, "", Color(0.52, 0.86, 1.0, 1.0))
	_add_menu_button(rows, "设置界面", "渲染风格和光照模式", _show_settings_menu, "", Color(1.0, 0.78, 0.42, 1.0))
	_add_menu_button(rows, "退出桌宠", "停止语音和屏幕识别后关闭程序", _request_app_quit, "", Color(1.0, 0.45, 0.55, 1.0))
	_add_menu_hint(rows, "提示：拖动空白处可移动菜单，拖动边框可缩放。Esc 关闭菜单。")


func _ensure_accessory_menu() -> void:
	if _accessory_menu_panel != null:
		return
	_ensure_menu_layer()
	_accessory_menu_panel = _make_menu_panel("KawaiiAccessoryMenu", main_menu_size, Color(0.55, 0.85, 1.0, 1.0))
	var rows := _make_menu_rows(_accessory_menu_panel)
	_add_menu_button(rows, "变身演出", "先收起再展开光翼和眼镜", _play_transformation_sequence, "TransformAccessoryButton", Color(0.78, 0.62, 1.0, 1.0))
	_add_menu_header(rows, "配饰开关", "快速切换外观", _show_main_menu_from_last_position)
	_add_menu_button(rows, "", "", _toggle_glasses_visible, "GlassesToggle", Color(0.55, 0.85, 1.0, 1.0))
	_add_menu_button(rows, "", "", _toggle_wings_visible, "WingsToggle", Color(0.72, 0.56, 1.0, 1.0))
	_add_menu_hint(rows, "头顶眼镜是固定装饰，不在这里关闭；这里控制眼部眼镜和翅膀。")
	_update_accessory_menu()


func _ensure_action_category_menu() -> void:
	if _action_category_menu_panel != null:
		return
	_ensure_menu_layer()
	_action_category_menu_panel = _make_menu_panel("KawaiiActionCategoryMenu", main_menu_size, Color(0.52, 0.86, 1.0, 1.0))
	var rows := _make_menu_rows(_action_category_menu_panel)
	_add_menu_header(rows, "浏览动作", "先选择用途分类", _show_main_menu_from_last_position)
	_add_menu_button(rows, "全部动作", "浏览当前启用的安全动作", func() -> void: _show_action_menu_category("全部"), "", Color(0.52, 0.86, 1.0, 1.0))
	_add_menu_button(rows, "基础待机", "空闲、呼吸、轻微观察", func() -> void: _show_action_menu_category("基础待机"), "", Color(0.74, 0.58, 1.0, 1.0))
	_add_menu_button(rows, "语音状态", "听、想、说、被打断和说话小动作", func() -> void: _show_action_menu_category("语音状态"), "", Color(0.68, 0.86, 1.0, 1.0))
	_add_menu_button(rows, "情绪反应", "开心、吐槽、生气、疑惑", func() -> void: _show_action_menu_category("情绪反应"), "", Color(1.0, 0.78, 0.42, 1.0))
	_add_menu_button(rows, "工作状态", "分析、查找、等待、完成", func() -> void: _show_action_menu_category("工作状态"), "", Color(0.55, 0.85, 1.0, 1.0))
	_add_menu_button(rows, "用户交互", "点击、拖动、看鼠标", func() -> void: _show_action_menu_category("用户交互"), "", Color(0.78, 0.62, 1.0, 1.0))
	_add_menu_button(rows, "桌宠姿态", "站姿和坐姿；趴/飞暂不开放", func() -> void: _show_action_menu_category("桌宠姿态"), "", Color(0.62, 0.82, 1.0, 1.0))
	_add_menu_button(rows, "特殊展示", "拍照、展示、变身类动作", func() -> void: _show_action_menu_category("特殊展示"), "", Color(1.0, 0.60, 0.72, 1.0))
	_add_menu_hint(rows, "Overlay 类如口型、眨眼、表情不在动作列表里，它们走表情/口型层。")


func _ensure_voice_menu() -> void:
	if _voice_menu_panel != null:
		return
	_ensure_menu_layer()
	_voice_menu_panel = _make_menu_panel("KawaiiVoiceMenu", main_menu_size, Color(0.78, 0.62, 1.0, 1.0))
	var rows := _make_menu_rows(_voice_menu_panel)
	_add_menu_header(rows, "语音对话", "选择语音链路", _show_main_menu_from_last_position)
	_add_menu_label(rows, "语音路线")
	_voice_route_s2s_button = _add_menu_button(rows, "", "", func() -> void: _select_voice_route("s2s_low_latency"), "VoiceRouteS2S", Color(0.78, 0.62, 1.0, 1.0))
	_voice_route_s2s_companion_button = _add_menu_button(rows, "", "", func() -> void: _select_voice_route("traditional_companion_polling"), "VoiceRouteS2SCompanion", Color(0.54, 0.86, 1.0, 1.0))
	_voice_route_traditional_button = _add_menu_button(rows, "", "", func() -> void: _select_voice_route("traditional_vision"), "VoiceRouteTraditional", Color(0.45, 0.78, 1.0, 1.0))
	_voice_route_agent_button = _add_menu_button(rows, "", "", func() -> void: _select_voice_route("agent_speaker"), "VoiceRouteAgentSpeaker", Color(0.95, 0.54, 0.82, 1.0))
	_update_voice_route_buttons()
	_add_menu_label(rows, "控制")
	_voice_start_button = _add_menu_button(rows, "", "", _request_voice_chat, "VoiceStartButton", Color(0.78, 0.62, 1.0, 1.0))
	_update_voice_route_buttons()
	_screen_vision_button = _add_menu_button(rows, "", "", _toggle_screen_vision, "VoiceScreenVisionToggle", Color(0.45, 0.78, 1.0, 1.0))
	_update_screen_vision_button()
	_companion_polling_button = _add_menu_button(rows, "", "", _toggle_companion_polling, "VoiceCompanionPollingToggle", Color(0.54, 0.86, 1.0, 1.0))
	_update_companion_polling_button()
	_add_menu_label(rows, "陪玩轮询间隔")
	_companion_interval_buttons.clear()
	_companion_interval_buttons[5] = _add_menu_button(rows, "", "节奏快，适合强陪玩；可能更容易打断思路", func() -> void: _set_companion_polling_interval(5), "VoiceCompanionInterval5", Color(0.54, 0.86, 1.0, 1.0))
	_companion_interval_buttons[10] = _add_menu_button(rows, "", "默认档，陪伴感和延迟比较均衡", func() -> void: _set_companion_polling_interval(10), "VoiceCompanionInterval10", Color(0.54, 0.86, 1.0, 1.0))
	_companion_interval_buttons[15] = _add_menu_button(rows, "", "节奏慢，适合少插话、减少打断", func() -> void: _set_companion_polling_interval(15), "VoiceCompanionInterval15", Color(0.54, 0.86, 1.0, 1.0))
	_update_companion_interval_buttons()
	_add_menu_button(rows, "API 服务商", "更改传统视觉 LLM 的接口、Key、模型和思考模式", _show_api_settings_menu, "", Color(0.52, 0.86, 1.0, 1.0))
	_add_menu_button(rows, "停止语音对话", "停止本地语音运行时", _request_voice_stop, "", Color(0.45, 0.78, 1.0, 1.0))
	_add_menu_button(rows, "麦克风输入", "默认使用系统麦克风", Callable(), "", Color(0.45, 0.78, 1.0, 1.0))
	_add_menu_button(rows, "对话人设", "银狼人设 + 复刻音色", Callable(), "", Color(0.95, 0.54, 0.82, 1.0))
	_add_menu_hint(rows, "三条路线互相独立：端到端负责低延迟对话；方舟视觉负责看屏幕；Agent 外挂只做发声、气泡和表演。")


func _ensure_api_settings_menu() -> void:
	if _api_settings_menu_panel != null:
		return
	_ensure_menu_layer()
	_api_settings_menu_panel = _make_menu_panel("KawaiiApiSettingsMenu", action_menu_size, Color(0.52, 0.86, 1.0, 1.0))
	var rows := _make_menu_rows(_api_settings_menu_panel)
	_add_menu_header(rows, "API 服务商", "传统视觉 LLM 配置", _show_voice_menu)

	_api_provider_option = _add_menu_option(
		rows,
		"服务商预设",
		["MiMo V2.5", "Qwen / DashScope", "自定义 OpenAI 兼容"],
		"MiMo V2.5",
		_on_api_provider_selected
	)
	_api_url_edit = _add_menu_line_edit(rows, "接口地址", "https://.../v1/chat/completions")
	_api_key_edit = _add_menu_line_edit(rows, "API Key", "粘贴新的 key")
	_api_key_edit.secret = true
	_api_model_edit = _add_menu_line_edit(rows, "模型名", "mimo-v2.5")
	_api_thinking_option = _add_menu_option(rows, "思考模式", ["disabled", "auto"], "disabled", Callable())
	_api_test_button = _add_menu_button(rows, "测试 API Key", "用当前输入发一个最小请求，不启动语音", _test_api_key_from_menu, "", Color(0.52, 0.86, 1.0, 1.0))
	_add_menu_button(rows, "保存传统视觉配置", "保存后请重新开始语音/陪玩视觉", _save_api_settings_from_menu, "", Color(0.74, 0.58, 1.0, 1.0))
	_add_menu_button(rows, "切到传统视觉路线", "保存前后都可以先切路线", func() -> void: _select_voice_route("traditional_vision"), "", Color(0.45, 0.78, 1.0, 1.0))
	_add_menu_hint(rows, "这里只改传统 ASR→视觉 LLM→TTS 路线，不会修改端到端 S2S，也不会修改 TTS 复刻音色。Key 会写入本地 local 配置，请不要把该文件发给别人。")
	_load_api_settings_into_menu()


func _ensure_settings_menu() -> void:
	if _settings_menu_panel != null:
		return
	_ensure_menu_layer()
	_settings_menu_panel = _make_menu_panel("KawaiiSettingsMenu", action_menu_size, Color(1.0, 0.78, 0.42, 1.0))
	var rows := _make_menu_rows(_settings_menu_panel)
	_add_menu_header(rows, "设置界面", "渲染氛围和光照方案", _show_main_menu_from_last_position)
	_add_menu_label(rows, "渲染风格")
	_add_menu_button(rows, "柔和桌宠", "陪伴感更软，阴影更轻", func() -> void: _set_anime_render_preset("DesktopPetSoft"), "", Color(0.95, 0.54, 0.82, 1.0))
	_add_menu_button(rows, "动画赛璐璐", "色块更明显，描边更强", func() -> void: _set_anime_render_preset("CelAnime"), "", Color(0.52, 0.86, 1.0, 1.0))
	_add_menu_button(rows, "星铁风格", "蓝紫边缘光 + 发光 Bloom", func() -> void: _set_anime_render_preset("StarRailStyle"), "", Color(0.48, 0.68, 1.0, 1.0))
	_add_menu_button(rows, "扁平立绘感", "减少体积感，更接近 2D", func() -> void: _set_anime_render_preset("FlatLive2DLike"), "", Color(0.74, 0.58, 1.0, 1.0))
	_add_menu_label(rows, "光照")
	_add_menu_button(rows, "跟随系统", "读取 Windows 明暗偏好", func() -> void: _set_light_mode(0), "", Color(0.68, 0.86, 1.0, 1.0))
	_add_menu_button(rows, "柔和", "默认陪伴光照", func() -> void: _set_light_mode(1), "", Color(0.95, 0.54, 0.82, 1.0))
	_add_menu_button(rows, "明亮", "更亮的测试光照", func() -> void: _set_light_mode(2), "", Color(1.0, 0.78, 0.42, 1.0))
	_add_menu_button(rows, "偏暗", "低亮度预览", func() -> void: _set_light_mode(3), "", Color(0.56, 0.58, 0.86, 1.0))
	_add_menu_button(rows, "渲染调试", "实时调相机、灯光、环境、Glow 和描边", _show_render_debug_menu, "", Color(0.52, 0.86, 1.0, 1.0))


func _ensure_render_debug_menu() -> void:
	if _render_debug_menu_panel != null:
		return
	_ensure_menu_layer()
	_render_debug_menu_panel = _make_menu_panel("KawaiiRenderDebugMenu", render_debug_menu_size, Color(0.52, 0.86, 1.0, 1.0))
	var rows := _make_menu_rows(_render_debug_menu_panel)
	_add_menu_header(rows, "渲染调试", "只调当前运行画面，不改模型资源", _show_settings_menu)

	_add_menu_label(rows, "预设")
	_render_preset_option = _add_menu_option(rows, "渲染预设", _render_preset_names(), _current_render_preset(), _on_render_preset_selected)
	_add_menu_button(rows, "重新应用当前预设", "如果材质或灯光被调乱，点这里回到预设状态", _apply_current_render_preset, "", Color(0.74, 0.58, 1.0, 1.0))
	_add_menu_button(rows, "切到星铁风格", "Bloom、蓝紫边缘光和发光配件", func() -> void: _set_anime_render_preset("StarRailStyle"), "", Color(0.48, 0.68, 1.0, 1.0))

	_add_menu_label(rows, "光照")
	_render_light_mode_option = _add_menu_option(rows, "光照模式", ["跟随系统", "柔和", "明亮", "偏暗"], _light_mode_name(_current_light_mode()), _on_render_light_mode_selected)
	_add_menu_slider(rows, "主光亮度", 0.0, 2.2, 0.01, _light_energy(_get_render_key_light(), 0.54), _set_key_light_energy)
	_add_menu_slider(rows, "边缘光亮度", 0.0, 2.0, 0.01, _light_energy(_get_render_rim_light(), 0.42), _set_rim_light_energy)
	_add_menu_slider(rows, "环境光", 0.0, 1.2, 0.01, _environment_float("ambient_light_energy", 0.32), _set_environment_ambient_energy)
	_add_menu_slider(rows, "曝光", 0.55, 1.45, 0.01, _environment_float("tonemap_exposure", 0.95), _set_environment_exposure)

	_add_menu_label(rows, "镜头和后期")
	_add_menu_slider(rows, "相机大小", 1.4, 4.2, 0.01, _camera_size(2.75), _set_camera_size)
	_add_menu_check(rows, "Glow / Bloom", _environment_bool("glow_enabled", false), _set_environment_glow_enabled)
	_add_menu_slider(rows, "Glow 强度", 0.0, 2.0, 0.01, _environment_float("glow_intensity", 0.45), _set_environment_glow_intensity)
	_add_menu_slider(rows, "Bloom 扩散", 0.0, 2.0, 0.01, _environment_float("glow_strength", 0.85), _set_environment_glow_strength)
	_add_menu_check(rows, "描边", _render_outline_enabled(), _set_render_outline_enabled)
	_add_menu_hint(rows, "这些滑杆是运行时调试入口：适合你现场找数值。满意后再把数值写回 res://config/anime_render_presets.json。")


func _add_status_hud_toggle_to_menu(rows: VBoxContainer) -> void:
	_add_menu_label(rows, "动作")
	_idle_return_button = _add_menu_button(rows, "", "", _toggle_auto_return_to_idle, "IdleReturnToggle", Color(0.52, 0.86, 1.0, 1.0))
	_update_idle_return_button()
	_add_menu_label(rows, "界面")
	_status_hud_button = _add_menu_button(rows, "", "", _toggle_status_hud, "StatusHudToggle", Color(0.95, 0.54, 0.82, 1.0))
	_update_status_hud_button()
	_speech_bubble_button = _add_menu_button(rows, "", "", _toggle_speech_bubble, "SpeechBubbleToggle", Color(0.74, 0.58, 1.0, 1.0))
	_update_speech_bubble_button()


func _ensure_status_hud_toggle_in_settings_menu() -> void:
	if _settings_menu_panel == null:
		return
	if _status_hud_button != null and _speech_bubble_button != null:
		_update_status_hud_button()
		_update_speech_bubble_button()
		return
	var rows := _find_menu_rows(_settings_menu_panel)
	if rows == null:
		return
	_add_status_hud_toggle_to_menu(rows)


func _show_main_menu_from_last_position() -> void:
	_ensure_main_menu()
	_show_menu_panel(_main_menu_panel, _last_menu_position)


func _make_menu_panel(panel_name: String, panel_size: Vector2, _accent_color: Color) -> PanelContainer:
	var panel := PanelContainer.new()
	panel.name = panel_name
	panel.custom_minimum_size = panel_size
	panel.size = panel_size
	panel.visible = false
	panel.mouse_filter = Control.MOUSE_FILTER_STOP
	var style := StyleBoxFlat.new()
	style.bg_color = MENU_BG
	style.border_color = MENU_BORDER
	style.set_border_width_all(1)
	style.set_corner_radius_all(10)
	style.shadow_color = Color(0, 0, 0, 0.4)
	style.shadow_size = 18
	style.shadow_offset = Vector2(0, 4)
	panel.add_theme_stylebox_override("panel", style)
	if menu_use_native_window:
		_menu_window_root.add_child(panel)
	else:
		_menu_layer.add_child(panel)
	return panel


func _make_menu_rows(panel: PanelContainer) -> VBoxContainer:
	var margin := MarginContainer.new()
	margin.name = "MenuMargin"
	margin.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	margin.size_flags_vertical = Control.SIZE_EXPAND_FILL
	margin.add_theme_constant_override("margin_left", 16)
	margin.add_theme_constant_override("margin_top", 16)
	margin.add_theme_constant_override("margin_right", 16)
	margin.add_theme_constant_override("margin_bottom", 16)
	panel.add_child(margin)

	var scroll := ScrollContainer.new()
	scroll.name = "MenuScroll"
	scroll.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	scroll.vertical_scroll_mode = ScrollContainer.SCROLL_MODE_AUTO
	scroll.follow_focus = true
	margin.add_child(scroll)

	var rows := VBoxContainer.new()
	rows.name = "MenuRows"
	rows.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	rows.size_flags_vertical = Control.SIZE_EXPAND_FILL
	rows.add_theme_constant_override("separation", 10)
	scroll.add_child(rows)
	return rows


func _find_menu_rows(panel: Control) -> VBoxContainer:
	if panel == null:
		return null
	return panel.find_child("MenuRows", true, false) as VBoxContainer


func _add_menu_header(rows: VBoxContainer, title: String, subtitle: String, back_or_close: Callable, close_text := "返回") -> void:
	var header_shell := PanelContainer.new()
	header_shell.mouse_filter = Control.MOUSE_FILTER_PASS
	header_shell.custom_minimum_size = Vector2(0.0, 64.0)
	var header_style := StyleBoxFlat.new()
	header_style.bg_color = MENU_HEADER_BG
	header_style.border_color = Color(1.0, 1.0, 1.0, 0.08)
	header_style.set_border_width_all(1)
	header_style.set_corner_radius_all(8)
	header_style.content_margin_left = 12.0
	header_style.content_margin_right = 10.0
	header_style.content_margin_top = 8.0
	header_style.content_margin_bottom = 8.0
	header_shell.add_theme_stylebox_override("panel", header_style)
	rows.add_child(header_shell)

	var header := HBoxContainer.new()
	header.custom_minimum_size = Vector2(0.0, 48.0)
	header.mouse_filter = Control.MOUSE_FILTER_PASS
	header_shell.add_child(header)
	var title_box := VBoxContainer.new()
	title_box.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	title_box.mouse_filter = Control.MOUSE_FILTER_IGNORE
	header.add_child(title_box)
	var title_label := Label.new()
	title_label.text = title
	title_label.add_theme_font_size_override("font_size", action_menu_font_size + 5)
	title_label.add_theme_color_override("font_color", MENU_TEXT)
	title_box.add_child(title_label)
	var subtitle_label := Label.new()
	subtitle_label.text = subtitle
	subtitle_label.add_theme_font_size_override("font_size", action_menu_font_size - 4)
	subtitle_label.add_theme_color_override("font_color", MENU_TEXT_DIM)
	title_box.add_child(subtitle_label)
	var close_button := Button.new()
	close_button.text = close_text
	close_button.custom_minimum_size = Vector2(76.0, 40.0)
	close_button.add_theme_font_size_override("font_size", action_menu_font_size - 1)
	_style_menu_header_close_button(close_button)
	close_button.pressed.connect(back_or_close)
	header.add_child(close_button)


func _add_menu_button(rows: VBoxContainer, text: String, description: String, callback: Callable, node_name := "", accent_color := Color(0.74, 0.58, 1.0, 1.0)) -> Button:
	var button := Button.new()
	button.name = node_name if not node_name.is_empty() else text.replace(" ", "")
	button.text = text if description.is_empty() else "%s\n%s" % [text, description]
	button.alignment = HORIZONTAL_ALIGNMENT_LEFT
	button.custom_minimum_size = Vector2(0.0, 62.0)
	button.add_theme_font_size_override("font_size", action_menu_font_size)
	_style_menu_button(button, accent_color, 0.24)
	if callback.is_valid():
		button.pressed.connect(callback)
	else:
		button.disabled = true
	rows.add_child(button)
	return button


func _add_menu_option(rows: VBoxContainer, label_text: String, items: Array, selected_text: String, callback: Callable) -> OptionButton:
	_add_menu_label(rows, label_text)
	var option := OptionButton.new()
	option.custom_minimum_size = Vector2(0.0, 42.0)
	option.add_theme_font_size_override("font_size", action_menu_font_size)
	_style_menu_button(option, Color(0.52, 0.86, 1.0, 1.0), 0.20)
	for item in items:
		option.add_item(str(item))
	var selected_index := 0
	for i in range(option.item_count):
		if option.get_item_text(i) == selected_text:
			selected_index = i
			break
	if option.item_count > 0:
		option.select(selected_index)
	if callback.is_valid():
		option.item_selected.connect(func(index: int) -> void:
			callback.call(index)
		)
	rows.add_child(option)
	return option


func _add_menu_line_edit(rows: VBoxContainer, label_text: String, placeholder_text: String) -> LineEdit:
	_add_menu_label(rows, label_text)
	var line_edit := LineEdit.new()
	line_edit.placeholder_text = placeholder_text
	line_edit.custom_minimum_size = Vector2(0.0, 42.0)
	line_edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	line_edit.add_theme_font_size_override("font_size", action_menu_font_size)
	_style_menu_line_edit(line_edit)
	rows.add_child(line_edit)
	return line_edit


func _add_menu_slider(rows: VBoxContainer, label_text: String, min_value: float, max_value: float, step: float, initial_value: float, callback: Callable) -> HSlider:
	var header := HBoxContainer.new()
	header.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	rows.add_child(header)

	var label := Label.new()
	label.text = label_text
	label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	label.add_theme_font_size_override("font_size", action_menu_font_size - 1)
	label.add_theme_color_override("font_color", MENU_TEXT_DIM)
	header.add_child(label)

	var value_label := Label.new()
	value_label.text = "%.2f" % initial_value
	value_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
	value_label.custom_minimum_size = Vector2(64.0, 0.0)
	value_label.add_theme_font_size_override("font_size", action_menu_font_size - 2)
	value_label.add_theme_color_override("font_color", MENU_TEXT_DIM)
	header.add_child(value_label)

	var slider := HSlider.new()
	slider.min_value = min_value
	slider.max_value = max_value
	slider.step = step
	slider.value = clampf(initial_value, min_value, max_value)
	slider.custom_minimum_size = Vector2(0.0, 34.0)
	slider.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	if callback.is_valid():
		slider.value_changed.connect(func(value: float) -> void:
			value_label.text = "%.2f" % value
			callback.call(value)
		)
	rows.add_child(slider)
	return slider


func _add_menu_check(rows: VBoxContainer, text: String, initial_value: bool, callback: Callable) -> CheckButton:
	var check := CheckButton.new()
	check.text = text
	check.button_pressed = initial_value
	check.custom_minimum_size = Vector2(0.0, 42.0)
	check.add_theme_font_size_override("font_size", action_menu_font_size)
	check.add_theme_color_override("font_color", MENU_TEXT)
	if callback.is_valid():
		check.toggled.connect(func(enabled: bool) -> void:
			callback.call(enabled)
		)
	rows.add_child(check)
	return check


func _style_menu_button(button: Button, accent_color: Color, _strength: float) -> void:
	# normal — subtle visible background so buttons don't look "black"
	var normal := StyleBoxFlat.new()
	normal.bg_color = Color(1, 1, 1, 0.05)
	normal.border_color = Color(1, 1, 1, 0.08)
	normal.set_border_width_all(1)
	normal.set_corner_radius_all(8)
	normal.content_margin_left = 13.0
	normal.content_margin_right = 13.0
	normal.content_margin_top = 8.0
	normal.content_margin_bottom = 8.0
	button.add_theme_stylebox_override("normal", normal)

	# hover — subtle white overlay
	var hover := StyleBoxFlat.new()
	hover.bg_color = MENU_BTN_HOVER
	hover.border_color = accent_color.lerp(Color(1, 1, 1, 0.3), 0.5)
	hover.set_border_width_all(1)
	hover.set_corner_radius_all(8)
	hover.content_margin_left = 13.0
	hover.content_margin_right = 13.0
	hover.content_margin_top = 8.0
	hover.content_margin_bottom = 8.0
	button.add_theme_stylebox_override("hover", hover)

	# pressed — dimmer
	var pressed := StyleBoxFlat.new()
	pressed.bg_color = MENU_BTN_PRESSED
	pressed.border_color = Color(1, 1, 1, 0.04)
	pressed.set_border_width_all(1)
	pressed.set_corner_radius_all(8)
	pressed.content_margin_left = 13.0
	pressed.content_margin_right = 13.0
	pressed.content_margin_top = 8.0
	pressed.content_margin_bottom = 8.0
	button.add_theme_stylebox_override("pressed", pressed)

	# disabled — muted
	var disabled := StyleBoxFlat.new()
	disabled.bg_color = Color(0.3, 0.3, 0.3, 0.3)
	disabled.border_color = Color(1, 1, 1, 0.04)
	disabled.set_border_width_all(1)
	disabled.set_corner_radius_all(8)
	disabled.content_margin_left = 13.0
	disabled.content_margin_right = 13.0
	disabled.content_margin_top = 8.0
	disabled.content_margin_bottom = 8.0
	button.add_theme_stylebox_override("disabled", disabled)

	button.add_theme_color_override("font_color", MENU_TEXT)
	button.add_theme_color_override("font_hover_color", MENU_ACCENT)
	button.add_theme_color_override("font_pressed_color", MENU_ACCENT)
	button.add_theme_color_override("font_disabled_color", MENU_TEXT_DIM)


func _style_menu_header_close_button(btn: Button) -> void:
	var normal := StyleBoxFlat.new()
	normal.bg_color = Color(1, 1, 1, 0.06)
	normal.set_corner_radius_all(6)
	btn.add_theme_stylebox_override("normal", normal)

	var hover := StyleBoxFlat.new()
	hover.bg_color = Color(MENU_CLOSE_HOVER.r, MENU_CLOSE_HOVER.g, MENU_CLOSE_HOVER.b, 0.2)
	hover.set_corner_radius_all(6)
	btn.add_theme_stylebox_override("hover", hover)

	btn.add_theme_color_override("font_color", MENU_TEXT_DIM)
	btn.add_theme_color_override("font_hover_color", MENU_CLOSE_HOVER)
	btn.add_theme_color_override("font_pressed_color", MENU_CLOSE_HOVER)


func _style_menu_line_edit(line_edit: LineEdit) -> void:
	var normal := StyleBoxFlat.new()
	normal.bg_color = Color(1, 1, 1, 0.06)
	normal.border_color = Color(1, 1, 1, 0.12)
	normal.set_border_width_all(1)
	normal.set_corner_radius_all(8)
	normal.content_margin_left = 12.0
	normal.content_margin_right = 12.0
	normal.content_margin_top = 7.0
	normal.content_margin_bottom = 7.0
	line_edit.add_theme_stylebox_override("normal", normal)
	line_edit.add_theme_stylebox_override("focus", normal)
	line_edit.add_theme_color_override("font_color", MENU_TEXT)
	line_edit.add_theme_color_override("font_placeholder_color", MENU_TEXT_DIM)
	line_edit.add_theme_color_override("caret_color", MENU_ACCENT)


func _style_menu_item_list(item_list: ItemList) -> void:
	var panel := StyleBoxFlat.new()
	panel.bg_color = Color(1, 1, 1, 0.04)
	panel.border_color = Color(1, 1, 1, 0.08)
	panel.set_border_width_all(1)
	panel.set_corner_radius_all(8)
	panel.content_margin_left = 8.0
	panel.content_margin_right = 8.0
	panel.content_margin_top = 8.0
	panel.content_margin_bottom = 8.0
	item_list.add_theme_stylebox_override("panel", panel)

	var selected := StyleBoxFlat.new()
	selected.bg_color = Color(0.4, 0.65, 1.0, 0.25)
	selected.border_color = Color(0.4, 0.65, 1.0, 0.4)
	selected.set_border_width_all(1)
	selected.set_corner_radius_all(8)
	item_list.add_theme_stylebox_override("selected", selected)
	item_list.add_theme_stylebox_override("selected_focus", selected)
	item_list.add_theme_color_override("font_color", MENU_TEXT)
	item_list.add_theme_color_override("font_selected_color", MENU_TEXT)


func _add_menu_label(rows: VBoxContainer, text: String) -> void:
	var label := Label.new()
	label.text = text
	label.add_theme_font_size_override("font_size", action_menu_font_size)
	label.add_theme_color_override("font_color", MENU_TEXT_DIM)
	rows.add_child(label)


func _add_menu_hint(rows: VBoxContainer, text: String) -> void:
	var label := Label.new()
	label.text = text
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	label.add_theme_font_size_override("font_size", action_menu_font_size - 3)
	label.add_theme_color_override("font_color", MENU_TEXT_MUTED)
	rows.add_child(label)


func _update_accessory_menu() -> void:
	if _accessory_menu_panel == null:
		return
	var glasses_button := _accessory_menu_panel.find_child("GlassesToggle", true, false) as Button
	if glasses_button != null:
		var is_on := _get_material_toggle_visible("get_glasses_visible", false)
		glasses_button.text = "%s 眼部眼镜\n变身眼镜开关" % [_check_mark(is_on)]
		_set_menu_button_active_style(glasses_button, is_on)
	var wings_button := _accessory_menu_panel.find_child("WingsToggle", true, false) as Button
	if wings_button != null:
		var is_on := _get_material_toggle_visible("get_wings_visible", false)
		wings_button.text = "%s 翅膀\n大型背饰显示开关" % [_check_mark(is_on)]
		_set_menu_button_active_style(wings_button, is_on)


func _check_mark(checked: bool) -> String:
	return "✔" if checked else "  "


func _set_menu_button_active_style(btn: Button, active: bool) -> void:
	if active:
		btn.add_theme_color_override("font_color", MENU_ACCENT)
	else:
		btn.add_theme_color_override("font_color", MENU_TEXT)


func _load_voice_routes_config() -> void:
	_voice_routes.clear()
	var path := voice_routes_config_path
	if path.is_empty() or not FileAccess.file_exists(path):
		voice_route_id = "s2s_low_latency"
		return
	var file := FileAccess.open(path, FileAccess.READ)
	if file == null:
		push_warning("无法读取语音路线配置：%s" % path)
		return
	var parsed = JSON.parse_string(file.get_as_text())
	if not (parsed is Dictionary):
		push_warning("语音路线配置不是 JSON 对象：%s" % path)
		return
	var routes = parsed.get("routes", {})
	if routes is Dictionary:
		_voice_routes = routes
	var default_route := str(parsed.get("default_route", "s2s_low_latency"))
	if _voice_routes.has(default_route):
		voice_route_id = default_route
	elif not _voice_routes.has(voice_route_id):
		voice_route_id = "s2s_low_latency"


func _load_api_settings_into_menu() -> void:
	if _api_url_edit == null or _api_key_edit == null or _api_model_edit == null or _api_thinking_option == null:
		return
	var config := _load_json_dict(traditional_llm_config_path)
	var llm_config := _read_nested_dict(config, ["StartVoiceChat", "Config", "LLMConfig"])
	var url := str(llm_config.get("Url", ""))
	var key := str(llm_config.get("APIKey", ""))
	var model := str(llm_config.get("ModelName", ""))
	var thinking := str(llm_config.get("ThinkingType", "auto"))
	_api_url_edit.text = url
	_api_key_edit.text = key
	_api_model_edit.text = model
	_select_menu_option_text(_api_thinking_option, thinking if not thinking.is_empty() else "auto")
	if _api_provider_option != null:
		_select_menu_option_text(_api_provider_option, _guess_api_provider_name(url, model))


func _on_api_provider_selected(index: int) -> void:
	if _api_provider_option == null:
		return
	var provider_name := _api_provider_option.get_item_text(index)
	var preset := _api_provider_preset(provider_name)
	if preset.is_empty():
		return
	if _api_url_edit != null:
		_api_url_edit.text = str(preset.get("url", _api_url_edit.text))
	if _api_model_edit != null:
		_api_model_edit.text = str(preset.get("model", _api_model_edit.text))
	if _api_thinking_option != null:
		_select_menu_option_text(_api_thinking_option, str(preset.get("thinking", "auto")))


func _save_api_settings_from_menu() -> void:
	var config := _load_json_dict(traditional_llm_config_path)
	if config.is_empty():
		config = {}
	var llm_config := _ensure_nested_dict(config, ["StartVoiceChat", "Config", "LLMConfig"])
	llm_config["Mode"] = "CustomLLM"
	llm_config["Url"] = _normalize_openai_chat_url(_api_url_edit.text.strip_edges() if _api_url_edit != null else str(llm_config.get("Url", "")))
	llm_config["ModelName"] = _api_model_edit.text.strip_edges() if _api_model_edit != null else str(llm_config.get("ModelName", ""))
	llm_config["ThinkingType"] = _selected_menu_option_text(_api_thinking_option, str(llm_config.get("ThinkingType", "auto")))

	var next_key := _api_key_edit.text.strip_edges() if _api_key_edit != null else ""
	if not next_key.is_empty():
		llm_config["APIKey"] = next_key

	if str(llm_config.get("Url", "")).is_empty() or str(llm_config.get("ModelName", "")).is_empty():
		_show_bubble_message("API 配置还缺接口地址或模型名。", 4.0)
		return

	_normalize_traditional_voice_config_numbers(config)
	var file := FileAccess.open(traditional_llm_config_path, FileAccess.WRITE)
	if file == null:
		push_warning("无法写入传统视觉 API 配置：%s" % traditional_llm_config_path)
		_show_bubble_message("API 配置保存失败，文件打不开。", 4.0)
		return
	file.store_string(JSON.stringify(config, "\t", false))
	_select_voice_route("traditional_vision")
	_load_api_settings_into_menu()
	_show_bubble_message("传统视觉 API 配置已保存。重新开始语音或陪玩视觉后生效。", 5.0)


func _test_api_key_from_menu() -> void:
	var url := _normalize_openai_chat_url(_api_url_edit.text.strip_edges() if _api_url_edit != null else "")
	var key := _api_key_edit.text.strip_edges() if _api_key_edit != null else ""
	var model := _api_model_edit.text.strip_edges() if _api_model_edit != null else ""
	if url.is_empty() or key.is_empty() or model.is_empty():
		_show_bubble_message("测试不了：接口地址、API Key、模型名都要填。", 4.0)
		return
	if _api_test_request != null:
		_api_test_request.cancel_request()
		_api_test_request.queue_free()
		_api_test_request = null
	_api_test_url = url
	_api_test_model = model
	_api_test_started_msec = Time.get_ticks_msec()
	_api_test_request = HTTPRequest.new()
	_api_test_request.name = "ApiKeyTestRequest"
	_api_test_request.timeout = 18.0
	add_child(_api_test_request)
	_api_test_request.request_completed.connect(_on_api_key_test_completed)

	var headers := _api_test_headers_for_url(url, key)
	var body := {
		"model": model,
		"messages": [
			{"role": "system", "content": "Reply with OK only."},
			{"role": "user", "content": "ping"},
		],
		"max_tokens": 8,
		"temperature": 0.1,
		"stream": false,
	}
	_set_api_test_button_busy(true)
	_show_bubble_message("正在测试 API Key……", 3.0)
	var error := _api_test_request.request(url, headers, HTTPClient.METHOD_POST, JSON.stringify(body))
	if error != OK:
		_cleanup_api_test_request()
		_set_api_test_button_busy(false)
		_show_bubble_message("测试请求没发出去：Godot HTTP 错误 %d。" % error, 5.0)


func _api_test_headers_for_url(url: String, key: String) -> PackedStringArray:
	var headers := PackedStringArray([
		"Content-Type: application/json",
		"Accept: application/json",
		"Authorization: Bearer %s" % key,
	])
	var lowered := url.to_lower()
	if lowered.contains("xiaomimimo"):
		headers.append("api-key: %s" % key)
	return headers


func _on_api_key_test_completed(result: int, response_code: int, _headers: PackedStringArray, body: PackedByteArray) -> void:
	var elapsed_sec := float(Time.get_ticks_msec() - _api_test_started_msec) / 1000.0
	var response_text := body.get_string_from_utf8()
	var message := _api_test_result_message(result, response_code, response_text, elapsed_sec)
	print("API key test result: result=%d http=%d model=%s elapsed=%.2fs body=%s" % [
		result,
		response_code,
		_api_test_model,
		elapsed_sec,
		_redact_api_test_body(response_text),
	])
	_cleanup_api_test_request()
	_set_api_test_button_busy(false)
	_show_bubble_message(message, 7.0)


func _api_test_result_message(result: int, response_code: int, response_text: String, elapsed_sec: float) -> String:
	if result != HTTPRequest.RESULT_SUCCESS:
		return "API 测试失败：网络/TLS/超时错误 %d，用时 %.1fs。" % [result, elapsed_sec]
	var error_message := _extract_api_error_message(response_text)
	if response_code >= 200 and response_code < 300:
		return "API Key 可用。模型 %s 响应正常，用时 %.1fs。" % [_api_test_model, elapsed_sec]
	if response_code == 401 or response_code == 403:
		return "API 测试失败：%d，Key 无效、过期或没权限。%s" % [response_code, error_message]
	if response_code == 404:
		return "API 测试失败：404，接口地址或模型名可能不对。%s" % error_message
	if response_code == 429:
		return "API 测试失败：429，额度或限流。%s" % error_message
	return "API 测试失败：HTTP %d。%s" % [response_code, error_message]


func _extract_api_error_message(response_text: String) -> String:
	var trimmed := response_text.strip_edges()
	if trimmed.is_empty():
		return ""
	var parsed = JSON.parse_string(trimmed)
	if parsed is Dictionary:
		var error = parsed.get("error")
		if error is Dictionary:
			var message := str(error.get("message", error.get("msg", ""))).strip_edges()
			return _short_error_text(message)
		var message := str(parsed.get("message", parsed.get("msg", ""))).strip_edges()
		return _short_error_text(message)
	return _short_error_text(trimmed)


func _short_error_text(text: String) -> String:
	var cleaned := text.replace("\n", " ").replace("\r", " ").strip_edges()
	if cleaned.length() > 120:
		return cleaned.substr(0, 120) + "…"
	return cleaned


func _redact_api_test_body(text: String) -> String:
	var cleaned := text.replace("\n", " ").replace("\r", " ")
	if cleaned.length() > 500:
		cleaned = cleaned.substr(0, 500) + "…"
	return cleaned


func _set_api_test_button_busy(busy: bool) -> void:
	if _api_test_button == null:
		return
	_api_test_button.disabled = busy
	_api_test_button.text = "测试中……\n等服务商回包" if busy else "测试 API Key\n用当前输入发一个最小请求，不启动语音"


func _cleanup_api_test_request() -> void:
	if _api_test_request == null:
		return
	_api_test_request.queue_free()
	_api_test_request = null


func _api_provider_preset(provider_name: String) -> Dictionary:
	match provider_name:
		"MiMo V2.5":
			return {
				"url": "https://token-plan-cn.xiaomimimo.com/v1/chat/completions",
				"model": "mimo-v2.5",
				"thinking": "disabled",
			}
		"Qwen / DashScope":
			return {
				"url": "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
				"model": "qwen3.6-flash",
				"thinking": "auto",
			}
		_:
			return {}


func _guess_api_provider_name(url: String, model: String) -> String:
	var lowered_url := url.to_lower()
	var lowered_model := model.to_lower()
	if lowered_url.contains("xiaomimimo") or lowered_model.contains("mimo"):
		return "MiMo V2.5"
	if lowered_url.contains("dashscope") or lowered_model.contains("qwen"):
		return "Qwen / DashScope"
	return "自定义 OpenAI 兼容"


func _normalize_openai_chat_url(url: String) -> String:
	var cleaned := url.strip_edges().trim_suffix("/")
	var lowered := cleaned.to_lower()
	if cleaned.is_empty():
		return cleaned
	if lowered.ends_with("/chat/completions"):
		return cleaned
	if lowered.ends_with("/v1") or lowered.ends_with("/compatible-mode/v1"):
		return "%s/chat/completions" % cleaned
	return cleaned


func _read_nested_dict(root: Dictionary, keys: Array) -> Dictionary:
	var current: Variant = root
	for key in keys:
		if not (current is Dictionary):
			return {}
		current = (current as Dictionary).get(str(key), {})
	if current is Dictionary:
		return current
	return {}


func _ensure_nested_dict(root: Dictionary, keys: Array) -> Dictionary:
	var current := root
	for key_variant in keys:
		var key := str(key_variant)
		var value = current.get(key)
		if not (value is Dictionary):
			value = {}
			current[key] = value
		current = value
	return current


func _select_menu_option_text(option: OptionButton, text: String) -> void:
	if option == null:
		return
	for i in range(option.item_count):
		if option.get_item_text(i) == text:
			option.select(i)
			return
	if option.item_count > 0:
		option.select(0)


func _selected_menu_option_text(option: OptionButton, fallback: String) -> String:
	if option == null or option.item_count <= 0:
		return fallback
	var selected := option.selected
	if selected < 0 or selected >= option.item_count:
		return fallback
	return option.get_item_text(selected)


func _normalize_traditional_voice_config_numbers(config: Dictionary) -> void:
	var start_voice_chat := _read_nested_dict(config, ["StartVoiceChat"])
	var voice_config := _read_nested_dict(config, ["StartVoiceChat", "Config"])
	var asr_params := _read_nested_dict(config, ["StartVoiceChat", "Config", "ASRConfig", "ProviderParams"])
	var vad_config := _read_nested_dict(config, ["StartVoiceChat", "Config", "ASRConfig", "VADConfig"])
	var interrupt_config := _read_nested_dict(config, ["StartVoiceChat", "Config", "ASRConfig", "InterruptConfig"])
	var llm_config := _read_nested_dict(config, ["StartVoiceChat", "Config", "LLMConfig"])
	var vision_config := _read_nested_dict(config, ["StartVoiceChat", "Config", "LLMConfig", "VisionConfig"])
	var snapshot_config := _read_nested_dict(config, ["StartVoiceChat", "Config", "LLMConfig", "VisionConfig", "SnapshotConfig"])
	var subtitle_config := _read_nested_dict(config, ["StartVoiceChat", "Config", "SubtitleConfig"])
	var companion_vision := _read_nested_dict(config, ["CompanionVision"])

	_force_dict_int(start_voice_chat, "InterruptMode")
	_force_dict_int(voice_config, "InterruptMode")
	_force_dict_int(asr_params, "StreamMode")
	_force_dict_int(vad_config, "SilenceTime")
	_force_dict_int(interrupt_config, "InterruptSpeechDuration")
	_force_dict_int(llm_config, "MaxTokens")
	_force_dict_int(llm_config, "HistoryLength")
	_force_dict_int(vision_config, "Height")
	_force_dict_int(vision_config, "Interval")
	_force_dict_int(vision_config, "ImagesLimit")
	_force_dict_int(snapshot_config, "StreamType")
	_force_dict_int(snapshot_config, "Height")
	_force_dict_int(snapshot_config, "Interval")
	_force_dict_int(snapshot_config, "ImagesLimit")
	_force_dict_int(subtitle_config, "SubtitleMode")
	_force_dict_int(companion_vision, "IntervalSec")
	_force_dict_int(companion_vision, "PendingTimeoutSec")
	_force_dict_int(companion_vision, "MaxBusyWithoutAudioSec")
	_remove_unsupported_start_voice_chat_llm_fields(llm_config, vision_config)


func _force_dict_int(dictionary: Dictionary, key: String) -> void:
	if dictionary.is_empty() or not dictionary.has(key):
		return
	var value = dictionary.get(key)
	if typeof(value) == TYPE_FLOAT or typeof(value) == TYPE_INT:
		dictionary[key] = int(round(float(value)))


func _remove_unsupported_start_voice_chat_llm_fields(llm_config: Dictionary, vision_config: Dictionary) -> void:
	if not llm_config.is_empty():
		llm_config.erase("Stream")
	if not vision_config.is_empty():
		for key in ["ImageDetail", "Height", "Interval", "ImagesLimit"]:
			vision_config.erase(key)


func _select_voice_route(next_route_id: String) -> void:
	if not _voice_routes.has(next_route_id):
		push_warning("未知语音路线：%s" % next_route_id)
		return
	voice_route_id = next_route_id
	_screen_vision_active = false
	_update_voice_route_buttons()
	_update_screen_vision_button()
	_update_companion_polling_button()
	var info := _voice_route_info(next_route_id)
	_show_bubble_message("已切到%s。%s" % [str(info.get("short_name", info.get("display_name", next_route_id))), str(info.get("description", ""))], 3.5)


func _voice_route_info(route_id: String = "") -> Dictionary:
	var id := route_id if not route_id.is_empty() else voice_route_id
	var value = _voice_routes.get(id, {})
	return value if value is Dictionary else {}


func _voice_route_short_name(route_id: String = "") -> String:
	var info := _voice_route_info(route_id)
	return str(info.get("short_name", info.get("display_name", route_id if not route_id.is_empty() else voice_route_id)))


func _voice_route_supports_vision() -> bool:
	return bool(_voice_route_info().get("supports_vision", false))


func _voice_route_kind(route_id: String = "") -> String:
	return str(_voice_route_info(route_id).get("kind", "voice_chat"))


func _voice_route_is_agent_speaker(route_id: String = "") -> bool:
	return _voice_route_kind(route_id) == "agent_speaker"


func _update_voice_route_buttons() -> void:
	var s2s_prefix := _check_prefix(voice_route_id == "s2s_low_latency")
	var s2s_companion_prefix := _check_prefix(voice_route_id == "traditional_companion_polling")
	var traditional_prefix := _check_prefix(voice_route_id == "traditional_vision")
	var agent_prefix := _check_prefix(voice_route_id == "agent_speaker")
	for panel in [_main_menu_panel, _voice_menu_panel]:
		if panel == null:
			continue
		var start_button := panel.find_child("VoiceStartButton", true, false) as Button
		if start_button != null:
			var start_title := "开启 Agent 外挂端口" if _voice_route_is_agent_speaker() else "开始语音对话"
			start_button.text = "%s（%s）\n%s" % [start_title, _voice_route_short_name(), str(_voice_route_info().get("description", "启动内置火山语音运行时"))]
		var s2s_button := panel.find_child("VoiceRouteS2S", true, false) as Button
		if s2s_button != null:
			s2s_button.text = "%s 端到端低延迟\nS2S 混合编排；不支持视觉，响应更快" % s2s_prefix
			_set_menu_button_active_style(s2s_button, voice_route_id == "s2s_low_latency")
		var s2s_companion_button := panel.find_child("VoiceRouteS2SCompanion", true, false) as Button
		if s2s_companion_button != null:
			s2s_companion_button.text = "%s MiMo 陪玩轮询\n独立测试路线；每 %d 秒主动看屏幕，等上一句说完再开口" % [s2s_companion_prefix, companion_polling_interval_sec]
			_set_menu_button_active_style(s2s_companion_button, voice_route_id == "traditional_companion_polling")
		var traditional_button := panel.find_child("VoiceRouteTraditional", true, false) as Button
		if traditional_button != null:
			traditional_button.text = "%s MiMo 视觉陪玩\n火山 ASR→MiMo 视觉 LLM→火山 TTS；支持屏幕视觉，延迟略高" % traditional_prefix
			_set_menu_button_active_style(traditional_button, voice_route_id == "traditional_vision")
		var agent_button := panel.find_child("VoiceRouteAgentSpeaker", true, false) as Button
		if agent_button != null:
			agent_button.text = "%s Agent MiMo 外挂\n外部 Agent 调 HTTP；本地 MiMo 发声、显示气泡和表演" % agent_prefix
			_set_menu_button_active_style(agent_button, voice_route_id == "agent_speaker")
	_update_companion_polling_button()
	_update_companion_interval_buttons()


func _toggle_status_hud() -> void:
	show_status_hud = not show_status_hud
	_apply_status_hud_visibility()
	_update_status_hud_button()


func _toggle_speech_bubble() -> void:
	show_speech_bubble = not show_speech_bubble
	_apply_speech_bubble_visibility()
	_update_speech_bubble_button()


func _toggle_auto_return_to_idle() -> void:
	auto_return_to_idle = not auto_return_to_idle
	_update_idle_return_button()
	_arm_idle_return_if_needed()


func _apply_status_hud_visibility() -> void:
	if _status_panel != null:
		_status_panel.visible = show_status_hud


func _apply_speech_bubble_visibility() -> void:
	if _speech_bubble == null:
		return
	if _speech_bubble.has_method("set_placeholder_enabled"):
		_speech_bubble.call("set_placeholder_enabled", not _voice_chat_active, true)
	if _speech_bubble.has_method("set_persistent_visible"):
		_speech_bubble.call("set_persistent_visible", not _voice_chat_active, speech_bubble_placeholder)
	if _speech_bubble.has_method("set_bubble_enabled"):
		_speech_bubble.call("set_bubble_enabled", show_speech_bubble)
	else:
		_speech_bubble.visible = show_speech_bubble


func _update_status_hud_button() -> void:
	if _status_hud_button != null:
		_status_hud_button.text = "%s 顶部信息条\n显示动作调试信息" % [_check_prefix(show_status_hud)]


func _update_speech_bubble_button() -> void:
	if _speech_bubble_button != null:
		_speech_bubble_button.text = "%s 常驻气泡\n显示对白气泡，可拖动位置" % [_check_prefix(show_speech_bubble)]


func _update_idle_return_button() -> void:
	if _idle_return_button != null:
		_idle_return_button.text = "%s 待机轮播\n回呼吸后 %.1f 秒随机小动作" % [_check_prefix(auto_return_to_idle and random_idle_enabled), random_idle_gap_sec]


func _check_prefix(value: bool) -> String:
	return "✔" if value else "  "


func _toggle_glasses_visible() -> void:
	var current := _get_material_toggle_visible("get_glasses_visible", false)
	_set_material_toggle_visible("set_glasses_visible", not current)
	_update_accessory_menu()


func _toggle_wings_visible() -> void:
	var current := _get_material_toggle_visible("get_wings_visible", false)
	_set_material_toggle_visible("set_wings_visible", not current)
	_update_accessory_menu()


func play_transformation() -> void:
	_play_transformation_sequence()


func _play_transformation_sequence() -> void:
	_transformation_sequence += 1
	var sequence := _transformation_sequence
	_hide_all_menus()

	if transformation_hide_before_reveal:
		_set_material_toggle_visible("set_glasses_visible", false)
		_set_material_toggle_visible("set_wings_visible", false)
		_update_accessory_menu()

	var action_index := _find_action_index_by_name(transformation_action_name)
	if action_index < 0:
		action_index = _find_action_index_by_name("KA_Idle35_FingerSnap")
	if action_index < 0:
		action_index = _find_action_index_by_name("KA_Idle36_Yay")
	if action_index >= 0:
		_request_action_by_index(action_index)

	var controller := get_node_or_null(expression_controller_path)
	if controller != null and controller.has_method("set_expression"):
		controller.call("set_expression", "happy", 1.0)

	await get_tree().create_timer(maxf(transformation_reveal_delay_sec, 0.0)).timeout
	if sequence != _transformation_sequence:
		return

	_set_material_toggle_visible("set_glasses_visible", true)
	_set_material_toggle_visible("set_wings_visible", true)
	_update_accessory_menu()


func _get_material_toggle_visible(method_name: String, fallback: bool) -> bool:
	var controller := get_node_or_null(material_toggle_controller_path)
	if controller != null and controller.has_method(method_name):
		return bool(controller.call(method_name))
	return fallback


func _set_material_toggle_visible(method_name: String, visible: bool) -> void:
	var controller := get_node_or_null(material_toggle_controller_path)
	if controller != null and controller.has_method(method_name):
		controller.call(method_name, visible)


func _set_anime_render_preset(next_preset_name: String) -> void:
	var controller := get_node_or_null(anime_render_controller_path)
	if controller != null and controller.has_method("set_preset_name"):
		controller.call("set_preset_name", next_preset_name)
		_apply_test_window_background()
		call_deferred("_apply_test_window_background")


func _set_light_mode(mode: int) -> void:
	var controller := get_node_or_null(lighting_controller_path)
	if controller != null and controller.has_method("set_light_mode"):
		controller.call("set_light_mode", mode)
		_apply_test_window_background()
		call_deferred("_apply_test_window_background")


func _render_preset_names() -> Array:
	var controller := _get_anime_render_controller()
	var names: Array = []
	if controller != null and controller.has_method("get_available_presets"):
		var raw_names = controller.call("get_available_presets")
		if raw_names is Array:
			for name in raw_names:
				names.append(str(name))
	names.sort()
	if names.is_empty():
		names = ["DesktopPetSoft", "CelAnime", "StarRailStyle", "FlatLive2DLike"]
	return names


func _current_render_preset() -> String:
	var controller := _get_anime_render_controller()
	if controller != null and controller.has_method("get_preset_name"):
		return str(controller.call("get_preset_name"))
	return "StarRailStyle"


func _on_render_preset_selected(index: int) -> void:
	if _render_preset_option == null or index < 0 or index >= _render_preset_option.item_count:
		return
	_set_anime_render_preset(_render_preset_option.get_item_text(index))


func _apply_current_render_preset() -> void:
	var controller := _get_anime_render_controller()
	if controller != null and controller.has_method("apply_current_preset"):
		controller.call("apply_current_preset")
		_apply_test_window_background()
		call_deferred("_apply_test_window_background")


func _current_light_mode() -> int:
	var controller := _get_lighting_controller()
	if controller != null and controller.has_method("get_light_mode"):
		return int(controller.call("get_light_mode"))
	return 1


func _light_mode_name(mode: int) -> String:
	match mode:
		0:
			return "跟随系统"
		2:
			return "明亮"
		3:
			return "偏暗"
		_:
			return "柔和"


func _on_render_light_mode_selected(index: int) -> void:
	_set_light_mode(index)


func _sync_render_debug_controls() -> void:
	if _render_preset_option != null:
		var current_preset := _current_render_preset()
		for i in range(_render_preset_option.item_count):
			if _render_preset_option.get_item_text(i) == current_preset:
				_render_preset_option.select(i)
				break
	if _render_light_mode_option != null:
		_render_light_mode_option.select(clampi(_current_light_mode(), 0, 3))


func _get_anime_render_controller() -> Node:
	return get_node_or_null(anime_render_controller_path)


func _get_lighting_controller() -> Node:
	return get_node_or_null(lighting_controller_path)


func _get_render_camera() -> Camera3D:
	var controller := _get_anime_render_controller()
	if controller != null:
		var path = controller.get("camera_path")
		if typeof(path) == TYPE_NODE_PATH:
			var camera := controller.get_node_or_null(path) as Camera3D
			if camera != null:
				return camera
	return get_node_or_null(camera_path) as Camera3D


func _get_render_world_environment() -> WorldEnvironment:
	var controller := _get_anime_render_controller()
	if controller != null:
		var path = controller.get("world_environment_path")
		if typeof(path) == TYPE_NODE_PATH:
			var world_environment := controller.get_node_or_null(path) as WorldEnvironment
			if world_environment != null:
				return world_environment
	return get_node_or_null("WorldEnvironment") as WorldEnvironment


func _get_render_key_light() -> DirectionalLight3D:
	var controller := _get_anime_render_controller()
	if controller != null:
		var path = controller.get("key_light_path")
		if typeof(path) == TYPE_NODE_PATH:
			var light := controller.get_node_or_null(path) as DirectionalLight3D
			if light != null:
				return light
	return get_node_or_null("KeyLight") as DirectionalLight3D


func _get_render_rim_light() -> DirectionalLight3D:
	var controller := _get_anime_render_controller()
	if controller != null:
		var path = controller.get("rim_light_path")
		if typeof(path) == TYPE_NODE_PATH:
			var light := controller.get_node_or_null(path) as DirectionalLight3D
			if light != null:
				return light
	return get_node_or_null("RimLight") as DirectionalLight3D


func _camera_size(fallback: float) -> float:
	var camera := _get_render_camera()
	if camera != null:
		return camera.size
	return fallback


func _light_energy(light: Light3D, fallback: float) -> float:
	if light != null:
		return light.light_energy
	return fallback


func _environment_object() -> Environment:
	var world_environment := _get_render_world_environment()
	if world_environment == null:
		return null
	return world_environment.environment


func _environment_float(property_name: String, fallback: float) -> float:
	var environment := _environment_object()
	if environment == null:
		return fallback
	var value = _get_object_property_value(environment, property_name, fallback)
	return float(value)


func _environment_bool(property_name: String, fallback: bool) -> bool:
	var environment := _environment_object()
	if environment == null:
		return fallback
	var value = _get_object_property_value(environment, property_name, fallback)
	return bool(value)


func _get_object_property_value(object: Object, property_name: String, fallback):
	if object == null:
		return fallback
	for property in object.get_property_list():
		if str(property.get("name", "")) == property_name:
			return object.get(property_name)
	return fallback


func _set_object_property_if_available(object: Object, property_name: String, value) -> void:
	if object == null:
		return
	for property in object.get_property_list():
		if str(property.get("name", "")) == property_name:
			object.set(property_name, value)
			return


func _set_key_light_energy(value: float) -> void:
	var light := _get_render_key_light()
	if light != null:
		light.light_energy = value


func _set_rim_light_energy(value: float) -> void:
	var light := _get_render_rim_light()
	if light != null:
		light.visible = value > 0.001
		light.light_energy = value


func _set_environment_ambient_energy(value: float) -> void:
	var environment := _environment_object()
	if environment != null:
		environment.ambient_light_energy = value
		_force_transparent_render_background()


func _set_environment_exposure(value: float) -> void:
	var environment := _environment_object()
	if environment != null:
		environment.tonemap_exposure = value
		_force_transparent_render_background()


func _set_camera_size(value: float) -> void:
	var camera := _get_render_camera()
	if camera != null:
		camera.size = value


func _set_environment_glow_enabled(enabled: bool) -> void:
	var environment := _environment_object()
	_set_object_property_if_available(environment, "glow_enabled", enabled)
	_force_transparent_render_background()


func _set_environment_glow_intensity(value: float) -> void:
	var environment := _environment_object()
	_set_object_property_if_available(environment, "glow_intensity", value)
	_force_transparent_render_background()


func _set_environment_glow_strength(value: float) -> void:
	var environment := _environment_object()
	_set_object_property_if_available(environment, "glow_strength", value)
	_force_transparent_render_background()


func _render_outline_enabled() -> bool:
	var controller := _get_anime_render_controller()
	if controller == null:
		return true
	return bool(controller.get("outline_enabled"))


func _set_render_outline_enabled(enabled: bool) -> void:
	var controller := _get_anime_render_controller()
	if controller == null:
		return
	controller.set("outline_enabled", enabled)
	if controller.has_method("apply_current_preset"):
		controller.call("apply_current_preset")
	_force_transparent_render_background()


func _force_transparent_render_background() -> void:
	var environment := _environment_object()
	if environment != null:
		environment.background_mode = Environment.BG_CLEAR_COLOR
		environment.background_color = Color(0.0, 0.0, 0.0, 0.0)
	_apply_test_window_background()


func _request_voice_chat() -> void:
	print("语音路线请求：启动本地运行时，路线=%s。" % voice_route_id)
	_voice_chat_active = true
	_apply_speech_bubble_visibility()
	_clear_voice_chat_placeholder()
	if _voice_route_is_agent_speaker():
		_show_bubble_message("Agent 外挂端口开启后，外部 Agent 发 POST /v1/say 就能让我开口。", 4.0)
	voice_chat_requested.emit(voice_route_id)


func _request_voice_stop() -> void:
	print("语音对话请求：停止内置火山语音运行时。")
	_voice_chat_active = false
	_screen_vision_active = false
	_apply_speech_bubble_visibility()
	_update_screen_vision_button()
	_update_companion_polling_button()
	voice_chat_stop_requested.emit()


func _toggle_screen_vision() -> void:
	if _screen_vision_active:
		_request_screen_vision_stop()
	else:
		_request_screen_vision_start()


func _toggle_companion_polling() -> void:
	if _screen_vision_active and voice_route_id == "traditional_companion_polling":
		_request_screen_vision_stop()
		return
	if voice_route_id != "traditional_companion_polling":
		_select_voice_route("traditional_companion_polling")
	_request_screen_vision_start()


func _request_screen_vision_start() -> void:
	var direct_vision_route := _direct_screen_vision_route_id()
	if not direct_vision_route.is_empty() and voice_route_id != direct_vision_route:
		_select_voice_route(direct_vision_route)
	if not _voice_route_supports_vision():
		print("屏幕识别请求被拒绝：当前端到端路线不支持视觉。")
		_screen_vision_active = false
		var route_text := "Agent MiMo 外挂只接外部 Agent 的结果，不自己看屏幕。" if _voice_route_is_agent_speaker() else "端到端路线不支持视觉。先切到“MiMo 视觉陪玩”。"
		_show_bubble_message(route_text, 4.0)
		_update_screen_vision_button()
		_update_companion_polling_button()
		return
	print("屏幕识别请求：开启传统视觉路线的 RTC 屏幕流。")
	_screen_vision_active = true
	_voice_chat_active = true
	_apply_speech_bubble_visibility()
	var vision_message := "陪玩视觉开启。选好共享窗口后，我会持续看屏幕。"
	if voice_route_id == "traditional_companion_polling":
		vision_message = "%d秒陪玩轮询开启。选好共享窗口后，我按这个节拍看一眼；上一句没说完就先等着。" % companion_polling_interval_sec
	_show_bubble_message(vision_message, 5.0)
	_update_screen_vision_button()
	_update_companion_polling_button()
	screen_vision_start_requested.emit(voice_route_id)


func _direct_screen_vision_route_id() -> String:
	if _voice_route_supports_vision() and not str(voice_route_id).begins_with("s2s"):
		return voice_route_id
	if _voice_routes.has("traditional_vision"):
		return "traditional_vision"
	for route_id in _voice_routes.keys():
		var route = _voice_routes.get(route_id, {})
		if route is Dictionary and bool(route.get("supports_vision", false)) and not str(route_id).begins_with("s2s"):
			return str(route_id)
	return ""


func _request_screen_vision_stop() -> void:
	print("屏幕识别请求：关闭 RTC 屏幕流视觉理解。")
	_screen_vision_active = false
	_show_bubble_message("屏幕识别关掉了。", 3.0)
	_update_screen_vision_button()
	_update_companion_polling_button()
	screen_vision_stop_requested.emit()


func _request_app_quit() -> void:
	print("退出桌宠：停止语音、屏幕识别并关闭程序。")
	var quit_signal_connections := app_quit_requested.get_connections()
	# 立即缩到 1px 视觉消失，不中断事件循环
	get_viewport().transparent_bg = true
	DisplayServer.window_set_size(Vector2i(1, 1))

	if _screen_vision_active:
		_screen_vision_active = false
		screen_vision_stop_requested.emit()
	_voice_chat_active = false
	voice_chat_stop_requested.emit()
	_update_screen_vision_button()
	_update_companion_polling_button()
	_hide_all_menus()
	app_quit_requested.emit()
	if quit_signal_connections.is_empty():
		push_warning("No app_quit_requested handler; falling back to direct quit.")
		await get_tree().create_timer(1.0).timeout
		get_tree().quit()


func _update_screen_vision_button() -> void:
	var buttons: Array[Button] = []
	if _screen_vision_button != null:
		buttons.append(_screen_vision_button)
	for panel in [_main_menu_panel, _voice_menu_panel]:
		if panel == null:
			continue
		for node_name in ["ScreenVisionToggle", "VoiceScreenVisionToggle"]:
			var found := panel.find_child(node_name, true, false) as Button
			if found != null and not buttons.has(found):
				buttons.append(found)
	if buttons.is_empty():
		return
	var text := ""
	var tooltip := ""
	if _voice_route_supports_vision():
		var active_text := "[x]" if _screen_vision_active else "[ ]"
		if voice_route_id == "traditional_companion_polling":
			text = "%s %d秒主动陪玩\n发布屏幕流，并按节拍主动看屏幕开口" % [active_text, companion_polling_interval_sec]
			tooltip = "MiMo 陪玩轮询路线：上一句没说完会等待，不会和普通视觉路线混在一起。"
		else:
			text = "%s 屏幕识别\n发布低清屏幕流；普通视觉不主动轮询" % active_text
			tooltip = "传统视觉路线会发布低清屏幕流；需要主动轮询时请点“主动陪玩”。"
	elif _voice_route_is_agent_speaker():
		text = "[ ] 屏幕识别（外挂路线不用）"
		tooltip = "Agent 外挂路线只接收外部 Agent 的文字、姿势和语音请求，不自己采集屏幕。"
	else:
		text = "[ ] 屏幕识别（端到端不可用）"
		tooltip = "端到端 S2S 当前不支持视觉；切到火山方舟视觉后可用。"
	for button in buttons:
		button.text = text
		button.tooltip_text = tooltip


func _update_companion_polling_button() -> void:
	if _companion_polling_button == null:
		return
	var active := _screen_vision_active and voice_route_id == "traditional_companion_polling"
	_companion_polling_button.text = "%s %d秒主动陪玩\n自动切到 MiMo 陪玩轮询，并打开屏幕识别" % [_check_prefix(active), companion_polling_interval_sec]
	_companion_polling_button.tooltip_text = "一键启动传统 ASR→MiMo 视觉→火山 TTS 的主动陪玩路线。需要在弹窗里选择共享屏幕或窗口。"
	_set_menu_button_active_style(_companion_polling_button, active)


func _set_companion_polling_interval(seconds: int) -> void:
	var normalized := _normalize_companion_polling_interval(seconds)
	if companion_polling_interval_sec == normalized:
		_update_companion_interval_buttons()
		return
	companion_polling_interval_sec = normalized
	print("陪玩轮询间隔：%d秒" % companion_polling_interval_sec)
	_update_voice_route_buttons()
	_update_screen_vision_button()
	_update_companion_polling_button()
	_update_companion_interval_buttons()
	companion_polling_interval_requested.emit(companion_polling_interval_sec)


func _normalize_companion_polling_interval(seconds: int) -> int:
	if seconds <= 7:
		return 5
	if seconds <= 12:
		return 10
	return 15


func _update_companion_interval_buttons() -> void:
	if _companion_interval_buttons.is_empty():
		return
	for key in _companion_interval_buttons.keys():
		var interval := int(key)
		var button := _companion_interval_buttons[key] as Button
		if button == null:
			continue
		var active := companion_polling_interval_sec == interval
		button.text = "%s %d 秒" % [_check_prefix(active), interval]
		button.tooltip_text = "当前陪玩轮询间隔：%d 秒" % interval
		_set_menu_button_active_style(button, active)


func _show_bubble_message(text: String, duration_sec: float) -> void:
	if _speech_bubble == null:
		return
	if _speech_bubble.has_method("set_placeholder_enabled"):
		_speech_bubble.call("set_placeholder_enabled", false, true)
	if _speech_bubble.has_method("show_text"):
		_speech_bubble.call("show_text", text, duration_sec)


func _clear_voice_chat_placeholder() -> void:
	if _speech_bubble == null:
		return
	if _speech_bubble.has_method("set_placeholder_enabled"):
		_speech_bubble.call("set_placeholder_enabled", false, true)
	if _speech_bubble.has_method("clear_text_without_placeholder"):
		_speech_bubble.call("clear_text_without_placeholder")
		return
	if _speech_bubble.has_method("hide_text"):
		_speech_bubble.call("hide_text", false)


func _ensure_action_menu_cn() -> void:
	if _action_menu_panel != null:
		return
	_ensure_menu_layer()
	_action_menu_panel = _make_menu_panel("KawaiiActionMenu", action_menu_size, Color(0.52, 0.86, 1.0, 1.0))
	var rows := _make_menu_rows(_action_menu_panel)
	_add_menu_header(rows, "动作列表", "当前分类下的可用动作", _show_action_category_menu_from_last_position)

	_action_menu_search = LineEdit.new()
	_action_menu_search.placeholder_text = "搜索：听 / 想 / 说话 / 吐槽 / 点击 / 展示"
	_action_menu_search.custom_minimum_size = Vector2(0.0, 38.0)
	_action_menu_search.add_theme_font_size_override("font_size", action_menu_font_size)
	_style_menu_line_edit(_action_menu_search)
	_action_menu_search.text_changed.connect(func(_text: String) -> void: _populate_action_menu_cn())
	rows.add_child(_action_menu_search)

	_action_menu_category = OptionButton.new()
	_action_menu_category.custom_minimum_size = Vector2(0.0, 38.0)
	_action_menu_category.add_theme_font_size_override("font_size", action_menu_font_size)
	_style_menu_button(_action_menu_category, Color(0.52, 0.86, 1.0, 1.0), 0.16)
	_action_menu_category.item_selected.connect(func(_index: int) -> void: _populate_action_menu_cn())
	rows.add_child(_action_menu_category)

	_action_menu_list = ItemList.new()
	_action_menu_list.custom_minimum_size = Vector2(action_menu_size.x - 28.0, action_menu_size.y - 212.0)
	_action_menu_list.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_action_menu_list.add_theme_font_size_override("font_size", action_menu_font_size)
	_style_menu_item_list(_action_menu_list)
	_action_menu_list.item_activated.connect(_on_action_menu_item_activated)
	_action_menu_list.item_clicked.connect(func(index: int, _at_position: Vector2, mouse_button_index: int) -> void:
		if mouse_button_index == MOUSE_BUTTON_LEFT:
			_on_action_menu_item_activated(index)
	)
	rows.add_child(_action_menu_list)

	var footer := HBoxContainer.new()
	footer.custom_minimum_size = Vector2(0.0, 40.0)
	rows.add_child(footer)
	var prev_button := _make_action_menu_button("上一个")
	prev_button.pressed.connect(func() -> void:
		previous_action()
		_populate_action_menu_cn()
	)
	footer.add_child(prev_button)
	var next_button := _make_action_menu_button("下一个")
	next_button.pressed.connect(func() -> void:
		next_action()
		_populate_action_menu_cn()
	)
	footer.add_child(next_button)
	var play_button := _make_action_menu_button("暂停/继续")
	play_button.pressed.connect(func() -> void:
		_playing = not _playing
		_update_status(true)
	)
	footer.add_child(play_button)

	_action_menu_count = Label.new()
	_action_menu_count.add_theme_font_size_override("font_size", action_menu_font_size - 1)
	_action_menu_count.add_theme_color_override("font_color", MENU_TEXT_DIM)
	rows.add_child(_action_menu_count)
	_add_menu_hint(rows, "当前只启用安全待机系列；趴下、飞行和 Overlay 是预留层，不会在这里硬切。")


func _ensure_action_menu_v2() -> void:
	if _action_menu_panel != null:
		return
	_ensure_menu_layer()
	_action_menu_panel = _make_menu_panel("KawaiiActionMenu", action_menu_size, Color(0.52, 0.86, 1.0, 1.0))
	var rows := _make_menu_rows(_action_menu_panel)
	_add_menu_header(rows, "动作选择", "浏览并预览可爱动作", _show_main_menu_from_last_position)

	_action_menu_search = LineEdit.new()
	_action_menu_search.placeholder_text = "搜索动作：待机 / 坐下 / 睡觉 / 跳舞"
	_action_menu_search.custom_minimum_size = Vector2(0.0, 38.0)
	_action_menu_search.add_theme_font_size_override("font_size", action_menu_font_size)
	_action_menu_search.text_changed.connect(func(_text: String) -> void: _populate_action_menu())
	rows.add_child(_action_menu_search)

	_action_menu_category = OptionButton.new()
	_action_menu_category.custom_minimum_size = Vector2(0.0, 38.0)
	_action_menu_category.add_theme_font_size_override("font_size", action_menu_font_size)
	_action_menu_category.item_selected.connect(func(_index: int) -> void: _populate_action_menu())
	rows.add_child(_action_menu_category)

	_action_menu_list = ItemList.new()
	_action_menu_list.custom_minimum_size = Vector2(action_menu_size.x - 28.0, action_menu_size.y - 212.0)
	_action_menu_list.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_action_menu_list.add_theme_font_size_override("font_size", action_menu_font_size)
	_action_menu_list.item_activated.connect(_on_action_menu_item_activated)
	_action_menu_list.item_clicked.connect(func(index: int, _at_position: Vector2, mouse_button_index: int) -> void:
		if mouse_button_index == MOUSE_BUTTON_LEFT:
			_on_action_menu_item_activated(index)
	)
	rows.add_child(_action_menu_list)

	var footer := HBoxContainer.new()
	footer.custom_minimum_size = Vector2(0.0, 40.0)
	rows.add_child(footer)
	var prev_button := _make_action_menu_button("上一个")
	prev_button.pressed.connect(func() -> void:
		previous_action()
		_populate_action_menu()
	)
	footer.add_child(prev_button)
	var next_button := _make_action_menu_button("下一个")
	next_button.pressed.connect(func() -> void:
		next_action()
		_populate_action_menu()
	)
	footer.add_child(next_button)
	var play_button := _make_action_menu_button("暂停/继续")
	play_button.pressed.connect(func() -> void:
		_playing = not _playing
		_update_status(true)
	)
	footer.add_child(play_button)

	_action_menu_count = Label.new()
	_action_menu_count.add_theme_font_size_override("font_size", action_menu_font_size - 1)
	_action_menu_count.add_theme_color_override("font_color", MENU_TEXT_DIM)
	rows.add_child(_action_menu_count)


func _ensure_action_menu() -> void:
	if _action_menu_panel != null:
		return

	_ensure_menu_layer()
	_action_menu_panel = _make_menu_panel("KawaiiActionMenu", action_menu_size, Color(0.52, 0.86, 1.0, 1.0))

	var margin := MarginContainer.new()
	margin.add_theme_constant_override("margin_left", 12)
	margin.add_theme_constant_override("margin_top", 12)
	margin.add_theme_constant_override("margin_right", 12)
	margin.add_theme_constant_override("margin_bottom", 12)
	_action_menu_panel.add_child(margin)

	var rows := VBoxContainer.new()
	rows.add_theme_constant_override("separation", 8)
	margin.add_child(rows)

	var header := HBoxContainer.new()
	header.custom_minimum_size = Vector2(0.0, 38.0)
	rows.add_child(header)

	_action_menu_title = Label.new()
	_action_menu_title.text = "动作列表"
	_action_menu_title.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_action_menu_title.add_theme_font_size_override("font_size", action_menu_font_size + 4)
	header.add_child(_action_menu_title)

	var close_button := Button.new()
	close_button.text = "×"
	close_button.custom_minimum_size = Vector2(42.0, 36.0)
	close_button.add_theme_font_size_override("font_size", action_menu_font_size)
	close_button.pressed.connect(_hide_action_menu)
	header.add_child(close_button)

	_action_menu_search = LineEdit.new()
	_action_menu_search.placeholder_text = "搜索动作，比如 Idle / Sit / Sleep / Dance"
	_action_menu_search.custom_minimum_size = Vector2(0.0, 38.0)
	_action_menu_search.add_theme_font_size_override("font_size", action_menu_font_size)
	_action_menu_search.text_changed.connect(func(_text: String) -> void: _populate_action_menu())
	rows.add_child(_action_menu_search)

	_action_menu_category = OptionButton.new()
	_action_menu_category.custom_minimum_size = Vector2(0.0, 38.0)
	_action_menu_category.add_theme_font_size_override("font_size", action_menu_font_size)
	_action_menu_category.item_selected.connect(func(_index: int) -> void: _populate_action_menu())
	rows.add_child(_action_menu_category)

	_action_menu_list = ItemList.new()
	_action_menu_list.custom_minimum_size = Vector2(action_menu_size.x - 24.0, action_menu_size.y - 190.0)
	_action_menu_list.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_action_menu_list.add_theme_font_size_override("font_size", action_menu_font_size)
	_action_menu_list.item_activated.connect(_on_action_menu_item_activated)
	_action_menu_list.item_clicked.connect(func(index: int, _at_position: Vector2, mouse_button_index: int) -> void:
		if mouse_button_index == MOUSE_BUTTON_LEFT:
			_on_action_menu_item_activated(index)
	)
	rows.add_child(_action_menu_list)

	var footer := HBoxContainer.new()
	footer.custom_minimum_size = Vector2(0.0, 40.0)
	rows.add_child(footer)

	var prev_button := _make_action_menu_button("上一個")
	prev_button.pressed.connect(func() -> void:
		previous_action()
		_populate_action_menu()
	)
	footer.add_child(prev_button)

	var next_button := _make_action_menu_button("下一個")
	next_button.pressed.connect(func() -> void:
		next_action()
		_populate_action_menu()
	)
	footer.add_child(next_button)

	var play_button := _make_action_menu_button("暂停/继续")
	play_button.pressed.connect(func() -> void:
		_playing = not _playing
		_update_status(true)
	)
	footer.add_child(play_button)

	_action_menu_count = Label.new()
	_action_menu_count.add_theme_font_size_override("font_size", action_menu_font_size - 1)
	rows.add_child(_action_menu_count)


func _make_action_menu_button(text: String) -> Button:
	var button := Button.new()
	button.text = text
	button.custom_minimum_size = Vector2(118.0, 38.0)
	button.add_theme_font_size_override("font_size", action_menu_font_size)
	_style_menu_button(button, Color(0.95, 0.54, 0.82, 1.0), 0.18)
	return button


func _populate_action_menu_cn() -> void:
	if _action_menu_list == null:
		return
	_rebuild_action_categories_cn()
	_action_menu_list.clear()
	var search := _action_menu_search.text.strip_edges().to_lower() if _action_menu_search != null else ""
	var category := _selected_action_category_cn()
	var visible_count := 0
	for i in range(_action_entries.size()):
		var entry: Dictionary = _action_entries[i]
		var name := String(entry.get("name", ""))
		if not _action_matches_filter_cn(name, search, category):
			continue
		var prefix := "▶" if i == _action_index else "  "
		var item_index := _action_menu_list.add_item("%s%03d  [%s] %s" % [prefix, i + 1, _primary_category_for_action_name_cn(name), _display_action_name_cn(name)])
		_action_menu_list.set_item_metadata(item_index, i)
		if i == _action_index:
			_action_menu_list.select(item_index)
			_action_menu_list.ensure_current_is_visible()
		visible_count += 1
	if _action_menu_count != null:
		_action_menu_count.text = "显示 %d / 共 %d 个动作。单击列表项即可切换。" % [visible_count, _action_entries.size()]


func _rebuild_action_categories_cn() -> void:
	if _action_menu_category == null:
		return
	var selected := _selected_action_category_cn()
	var categories := {"全部": true}
	for entry in _action_entries:
		for category in _categories_for_action_name_cn(String(entry.get("name", ""))):
			categories[String(category)] = true
	var names := PackedStringArray()
	for ordered_name in ACTION_MENU_CATEGORY_ORDER:
		if categories.has(String(ordered_name)):
			names.append(String(ordered_name))
	var extra_names := PackedStringArray()
	for key in categories.keys():
		var name := String(key)
		if names.find(name) < 0:
			extra_names.append(name)
	extra_names.sort()
	for name in extra_names:
		names.append(name)
	if _category_names == names:
		return
	_category_names = names
	_action_menu_category.clear()
	for name in _category_names:
		_action_menu_category.add_item(name)
	var selected_index: int = maxi(0, _category_names.find(selected))
	_action_menu_category.select(selected_index)


func _selected_action_category_cn() -> String:
	if _action_menu_category == null or _action_menu_category.item_count <= 0:
		return "全部"
	return _action_menu_category.get_item_text(_action_menu_category.selected)


func _action_matches_filter_cn(name: String, search: String, category: String) -> bool:
	var categories := _categories_for_action_name_cn(name)
	if category != "全部" and categories.find(category) < 0:
		return false
	if search.is_empty():
		return true
	var searchable := "%s %s %s" % [name.to_lower(), _display_action_name_cn(name).to_lower(), " ".join(categories)]
	return searchable.to_lower().contains(search)


func _category_for_action_name_cn(name: String) -> String:
	return _primary_category_for_action_name_cn(name)


func _primary_category_for_action_name_cn(name: String) -> String:
	var categories := _categories_for_action_name_cn(name)
	if categories.is_empty():
		return "其他"
	return String(categories[0])


func _categories_for_action_name_cn(name: String) -> PackedStringArray:
	var categories := PackedStringArray()
	var idle_number := _idle_action_number(name)
	if idle_number in [1, 3, 4, 5, 9, 10, 11, 12, 15, 40, 46, 53]:
		categories.append("基础待机")
	if idle_number in [2, 8, 27, 28, 29, 42, 50, 51]:
		categories.append("语音状态")
	if idle_number in [18, 19, 25, 26, 27, 28, 29, 36, 37, 38, 41, 42, 43, 47]:
		categories.append("情绪反应")
	if idle_number in [2, 3, 8, 9, 19, 35, 36]:
		categories.append("工作状态")
	if idle_number in [2, 12, 21, 22, 39, 43, 44, 45, 52, 61, 62]:
		categories.append("用户交互")
	if idle_number in [1, 10, 40, 46, 53]:
		categories.append("桌宠姿态")
	if idle_number in [54]:
		categories.append("姿态过渡")
	if idle_number in [14, 23, 24, 35, 36, 41, 43, 54, 55, 56, 59, 60, 61, 62]:
		categories.append("特殊展示")
	if categories.is_empty():
		categories.append(_legacy_category_for_action_name_cn(name))
	return categories


func _legacy_category_for_action_name_cn(name: String) -> String:
	var lower := name.to_lower()
	if lower.contains("_idle"):
		return "基础待机"
	if lower.contains("_sit"):
		return "桌宠姿态"
	if lower.contains("_sleep"):
		return "睡眠预留"
	if lower.contains("_walk"):
		return "用户交互"
	if lower.contains("_run"):
		return "特殊展示"
	if lower.contains("_jump"):
		return "特殊展示"
	if lower.contains("_combat"):
		return "特殊展示"
	if lower.contains("_swimming"):
		return "特殊展示"
	if lower.contains("_fly"):
		return "特殊展示"
	if lower.contains("_death"):
		return "姿态过渡"
	if lower.contains("_zaxisonly"):
		return "用户交互"
	return "其他"


func _set_action_menu_category_cn(category: String) -> void:
	if _action_menu_category == null:
		return
	for i in range(_action_menu_category.item_count):
		if _action_menu_category.get_item_text(i) == category:
			_action_menu_category.select(i)
			return


func _display_action_name_cn(name: String) -> String:
	var display := name
	display = display.replace("KA_", "")
	display = display.replace("_action_bundle", "")
	var replacements := {
		"ZAxisOnly": "直线",
		"Combat": "战斗",
		"BareHands": "空手",
		"HeavySword": "重剑",
		"OHSword": "单手剑",
		"Witch": "魔法",
		"Magic": "魔法",
		"ChargeAttack": "蓄力攻击",
		"ComboAll": "连击全套",
		"Combo": "连击",
		"DamageAll": "受击全套",
		"Damage": "受击",
		"Awakening": "觉醒",
		"Impact": "冲击",
		"Recovery": "恢复",
		"Shot": "射击",
		"Unexploded": "未爆发",
		"Death": "倒地",
		"Ground": "地面",
		"Underwater": "水下",
		"Fly": "飞行",
		"Start": "开始",
		"Loop": "循环",
		"End": "结束",
		"Idle": "待机",
		"breathing": "呼吸",
		"LookLeftAndRight": "左右看",
		"LookAtHands": "看手",
		"LookAtFeet": "看脚",
		"Stretch": "伸懒腰",
		"JumpAround": "蹦跳",
		"SpinningJump": "旋转跳",
		"ComeUpWithAnIdea": "想到点子",
		"Waiting": "等待",
		"LookingBack": "回头",
		"LeaningForward": "前倾",
		"Dance": "跳舞",
		"TieShoelaces": "系鞋带",
		"WaveHands": "挥双手",
		"WaveHandSlightly": "轻轻挥手",
		"StumbleAndFall": "踉跄摔倒",
		"ShyRefusal": "害羞拒绝",
		"Shy": "害羞",
		"TriplePose": "三连姿势",
		"HighFive": "击掌",
		"Cheers": "欢呼",
		"Shout": "呼喊",
		"Angry": "生气",
		"Laugh": "大笑",
		"Surprised": "惊讶",
		"PickUp": "捡起",
		"LeanAgainst": "倚靠",
		"Hug": "拥抱",
		"FingerSnap": "响指",
		"Yay": "开心",
		"Tsundere": "傲娇",
		"Cry": "哭泣",
		"CuteArmUp": "可爱举手",
		"CrossLegged": "盘腿",
		"CrossLegs": "交叉腿",
		"CuteShyPose": "可爱害羞",
		"Taunt": "挑衅",
		"HandOnHip": "叉腰",
		"GreetingBow": "鞠躬",
		"Scaring": "吓人",
		"StandingTalk": "站立说话",
		"Curtsy": "屈膝礼",
		"Seiza": "正坐",
		"CartwheelAndBackHandspring": "侧手翻后手翻",
		"Backflip": "后空翻",
		"Handstand": "倒立",
		"Kiss": "亲吻",
		"RockPaperScissors": "猜拳",
		"Jump": "跳跃",
		"Run": "跑步",
		"Walk": "走路",
		"Bwd": "后退",
		"Left": "向左",
		"Right": "向右",
		"Pivot": "转身",
		"Stop": "停止",
		"SitFloor": "坐地",
		"Sit": "坐下",
		"LookAtToes": "看脚趾",
		"LookingAround": "四处看",
		"PutHandBetweens": "双手放中间",
		"WithBothLegsUp": "双腿抬起",
		"Skipping": "小跳步",
		"Sleep": "睡觉",
		"BendOneKnee": "单膝弯曲",
		"CurlUpSideways": "侧身蜷缩",
		"FaceDown": "趴下",
		"TurnToTheSide": "翻身",
		"Swimming": "游泳",
		"Crawl": "自由泳",
		"Fwd": "向前",
		"Diving": "潜水",
		"FlutterKick": "打水",
		"TurnLeft": "左转",
		"TurnRight": "右转",
	}
	for key in replacements.keys():
		display = display.replace(String(key), String(replacements[key]))
	display = display.replace("_", " ")
	return display.strip_edges()


func _populate_action_menu() -> void:
	if _action_menu_list == null:
		return
	_rebuild_action_categories()
	_action_menu_list.clear()
	var search := _action_menu_search.text.strip_edges().to_lower() if _action_menu_search != null else ""
	var category := _selected_action_category()
	var visible_count := 0
	for i in range(_action_entries.size()):
		var entry: Dictionary = _action_entries[i]
		var name := String(entry.get("name", ""))
		if not _action_matches_filter(name, search, category):
			continue
		var prefix := "▶ " if i == _action_index else "  "
		var item_index := _action_menu_list.add_item("%s%03d  %s" % [prefix, i + 1, name])
		_action_menu_list.set_item_metadata(item_index, i)
		if i == _action_index:
			_action_menu_list.select(item_index)
			_action_menu_list.ensure_current_is_visible()
		visible_count += 1
	if _action_menu_count != null:
		_action_menu_count.text = "显示 %d / 共 %d 个动作。双击或单击列表项切换。" % [visible_count, _action_entries.size()]


func _on_action_menu_item_activated(index: int) -> void:
	if _action_menu_list == null or index < 0 or index >= _action_menu_list.item_count:
		return
	var action_index := int(_action_menu_list.get_item_metadata(index))
	_request_action_by_index(action_index)
	_populate_action_menu_cn()


func _rebuild_action_categories() -> void:
	if _action_menu_category == null:
		return
	var selected := _selected_action_category()
	var categories := {"全部": true}
	for entry in _action_entries:
		categories[_category_for_action_name(String(entry.get("name", "")))] = true
	var names := PackedStringArray()
	for key in categories.keys():
		names.append(String(key))
	names.sort()
	if _category_names == names:
		return
	_category_names = names
	_action_menu_category.clear()
	for name in _category_names:
		_action_menu_category.add_item(name)
	var selected_index: int = maxi(0, _category_names.find(selected))
	_action_menu_category.select(selected_index)


func _selected_action_category() -> String:
	if _action_menu_category == null or _action_menu_category.item_count <= 0:
		return "全部"
	return _action_menu_category.get_item_text(_action_menu_category.selected)


func _action_matches_filter(name: String, search: String, category: String) -> bool:
	if category != "全部" and _category_for_action_name(name) != category:
		return false
	if search.is_empty():
		return true
	return name.to_lower().contains(search)


func _category_for_action_name(name: String) -> String:
	var lower := name.to_lower()
	if lower.contains("_idle"):
		return "Idle"
	if lower.contains("_sit"):
		return "Sit"
	if lower.contains("_sleep"):
		return "Sleep"
	if lower.contains("_walk"):
		return "Walk"
	if lower.contains("_run"):
		return "Run"
	if lower.contains("_jump"):
		return "Jump"
	if lower.contains("_combat"):
		return "Combat"
	if lower.contains("_swimming"):
		return "Swimming"
	if lower.contains("_fly"):
		return "Fly"
	if lower.contains("_death"):
		return "Death"
	if lower.contains("_zaxisonly"):
		return "ZAxisOnly"
	return "Other"


func _clamp_action_menu_position(position: Vector2) -> Vector2:
	return _clamp_menu_position(position, action_menu_size)


func _clamp_menu_position(position: Vector2, menu_size: Vector2) -> Vector2:
	var viewport_size := get_viewport().get_visible_rect().size
	return Vector2(
		clampf(position.x, 8.0, maxf(8.0, viewport_size.x - menu_size.x - 8.0)),
		clampf(position.y, 8.0, maxf(8.0, viewport_size.y - menu_size.y - 8.0))
	)


func _load_action_by_index(index: int) -> bool:
	if _action_entries.is_empty():
		return false
	var transition_start_pose := _capture_current_skeleton_pose()
	_action_index = posmod(index, _action_entries.size())
	action_bundle_path = String(_action_entries[_action_index].get("path", action_bundle_path))
	if not _load_action_bundle():
		return false
	_elapsed_sec = 0.0
	_action_wall_elapsed_sec = 0.0
	_playing = auto_play
	_start_action_transition(transition_start_pose)
	_apply_action_at_time(0.0)
	action_loaded.emit(_action_name)
	_apply_expression_for_action(_action_name)
	_arm_idle_return_if_needed()
	_sync_animation_director_current_action()
	_update_status(true)
	return true


func request_action(action_name: String) -> bool:
	var index := _find_action_index_by_name(action_name)
	if index < 0:
		push_warning("KawaiiActionPlayer action not found: %s" % action_name)
		return false
	return _load_action_by_index(index)


func can_interrupt_action_for_director(min_visible_sec := 0.0) -> bool:
	if _action_name.is_empty():
		return true
	if _is_base_idle_action(_action_name):
		return true
	var visible_sec := maxf(float(min_visible_sec), 0.0)
	if _action_wall_elapsed_sec < visible_sec:
		return false
	if _is_idle_insert_action(_action_name):
		return _action_wall_elapsed_sec >= maxf(_length_sec - 0.05, 0.0)
	if _should_loop_current_action():
		return _action_wall_elapsed_sec >= visible_sec
	return _action_wall_elapsed_sec >= maxf(_length_sec - 0.05, visible_sec)


func is_current_action_base_idle() -> bool:
	return _is_base_idle_action(_action_name)


func _request_action_by_index(index: int) -> bool:
	if _action_entries.is_empty():
		return false
	var normalized_index := posmod(index, _action_entries.size())
	var action_name := String(_action_entries[normalized_index].get("name", ""))
	var director := get_node_or_null(animation_director_path)
	if director != null and director.has_method("request_action"):
		if bool(director.call("request_action", action_name, false, true)):
			return true
	return _load_action_by_index(normalized_index)


func _find_action_index_by_name(action_name: String) -> int:
	for i in range(_action_entries.size()):
		var entry_name := String(_action_entries[i].get("name", ""))
		if entry_name == action_name:
			return i
	return -1


func _sync_animation_director_current_action() -> void:
	var director := get_node_or_null(animation_director_path)
	if director != null and director.has_method("sync_current_action"):
		director.call("sync_current_action", _action_name)


func _arm_idle_return_if_needed() -> void:
	_idle_return_armed = auto_return_to_idle and not _is_base_idle_action(_action_name)


func _process_idle_return() -> void:
	if not _idle_return_armed:
		return
	var action_duration := maxf(_length_sec, 0.0)
	var delay := 0.0 if _is_idle_insert_action(_action_name) else maxf(return_to_idle_delay_sec, 0.0)
	var return_threshold := action_duration + delay
	if _action_wall_elapsed_sec < return_threshold:
		return
	_idle_return_armed = false
	var idle_index := _find_action_index_by_name(return_to_idle_action_name)
	if idle_index >= 0:
		_request_action_by_index(idle_index)


func _process_random_idle() -> void:
	if not random_idle_enabled:
		return
	if not _is_base_idle_action(_action_name):
		return
	if _transition_active:
		return
	if _action_wall_elapsed_sec < maxf(random_idle_gap_sec, 0.0):
		return
	var next_idle := _pick_random_idle_action_name(_action_name)
	var next_index := _find_action_index_by_name(next_idle)
	if next_index >= 0:
		_request_action_by_index(next_index)


func _is_base_idle_action(action_name: String) -> bool:
	return action_name == return_to_idle_action_name


func _is_idle_insert_action(action_name: String) -> bool:
	return _is_random_idle_action(action_name) or _is_startup_idle_action(action_name)


func _is_random_idle_action(action_name: String) -> bool:
	for idle_name in random_idle_action_names:
		if String(idle_name) == action_name:
			return true
	return false


func _is_startup_idle_action(action_name: String) -> bool:
	for idle_name in startup_idle_action_names:
		if String(idle_name) == action_name:
			return true
	return false


func _should_loop_current_action() -> bool:
	if _is_base_idle_action(_action_name):
		return true
	if _is_idle_insert_action(_action_name):
		return false
	return loop


func _pick_random_idle_action_name(current_action_name: String) -> String:
	var candidates: Array[String] = []
	for idle_name_variant in random_idle_action_names:
		var idle_name := String(idle_name_variant)
		if idle_name.is_empty() or _find_action_index_by_name(idle_name) < 0:
			continue
		if idle_name == current_action_name and random_idle_action_names.size() > 1:
			continue
		candidates.append(idle_name)
	if candidates.is_empty():
		return return_to_idle_action_name
	return candidates[_rng.randi_range(0, candidates.size() - 1)]


func _pick_startup_idle_action_name() -> String:
	var candidates: Array[String] = []
	for idle_name_variant in startup_idle_action_names:
		var idle_name := String(idle_name_variant)
		if idle_name.is_empty() or _find_action_index_by_name(idle_name) < 0:
			continue
		candidates.append(idle_name)
	if candidates.is_empty():
		return return_to_idle_action_name
	return candidates[_rng.randi_range(0, candidates.size() - 1)]


func _apply_expression_for_action(action_name: String) -> void:
	if not auto_expression_from_action:
		return
	var controller := get_node_or_null(expression_controller_path)
	if controller == null or not controller.has_method("set_expression"):
		return
	var expression_name := _expression_name_for_action(action_name)
	if expression_name.is_empty():
		return
	controller.call("set_expression", expression_name, 1.0)


func _expression_name_for_action(action_name: String) -> String:
	var lower := action_name.to_lower()
	if lower.contains("sleep") or lower.contains("facedown") or lower.contains("curlup"):
		return "sleeping"
	if lower.contains("talk"):
		return "talk"
	if lower.contains("angry") or lower.contains("taunt") or lower.contains("scaring") or lower.contains("shout"):
		return "angry"
	if lower.contains("laugh") or lower.contains("yay") or lower.contains("cheers") or lower.contains("happy") or lower.contains("highfive"):
		return "happy"
	if lower.contains("idea") or lower.contains("thinking") or lower.contains("lookathands") or lower.contains("waiting") or lower.contains("fingersnap"):
		return "thinking"
	return "neutral"


func _load_model() -> bool:
	var packed = ResourceLoader.load(model_path)
	if packed == null or not (packed is PackedScene):
		_fail("Cannot load model as PackedScene: %s" % model_path)
		return false

	var parent := get_node_or_null(model_parent_path)
	if parent == null:
		parent = self

	_model_root = (packed as PackedScene).instantiate() as Node3D
	if _model_root == null:
		_fail("Model root is not Node3D: %s" % model_path)
		return false
	parent.add_child(_model_root)
	_stop_animation_players(_model_root)
	if auto_fit_to_view:
		_fit_model_to_view(_model_root)
	return true


func _bind_runtime_skeleton() -> bool:
	var skeletons: Array[Skeleton3D] = []
	_collect_skeletons(_model_root, skeletons)
	if skeletons.is_empty():
		_fail("No Skeleton3D found under loaded model.")
		return false

	_skeleton = _choose_skeleton(skeletons)
	if _skeleton == null:
		_fail("Could not choose runtime Skeleton3D.")
		return false

	_bone_lookup.clear()
	_bone_alias_lookup.clear()
	for bone_idx in range(_skeleton.get_bone_count()):
		var bone_name := _skeleton.get_bone_name(bone_idx)
		_bone_lookup[bone_name] = bone_idx
		var normalized := _normalize_bone_name_for_lookup(bone_name)
		if not normalized.is_empty() and not _bone_alias_lookup.has(normalized):
			_bone_alias_lookup[normalized] = bone_name

	_capture_runtime_rest_pose()
	return true


func _load_action_bundle() -> bool:
	_imported_rest_pose_map.clear()
	_action_bones.clear()
	_action_name = ""
	_frame_count = 0
	_length_sec = 0.0

	var data := _load_json_dict(action_bundle_path)
	if data.is_empty():
		_fail("Action bundle is empty or invalid: %s" % action_bundle_path)
		return false

	_action_name = str(data.get("action_name", action_bundle_path.get_file().get_basename()))
	_sample_rate = maxf(float(data.get("sample_rate", 30.0)), 1.0)
	_source_sample_rate = _sample_rate
	_frame_count = int(data.get("frame_count", 0))
	_length_sec = maxf(float(data.get("length_sec", 0.0)), 0.001)
	loop = bool(data.get("loop", loop))

	_imported_rest_pose_map = _parse_imported_rest_pose(data.get("rest_pose", {}))
	_action_bones = _parse_action_bones(data.get("actions", []))
	if _action_bones.is_empty():
		_fail("No action bones mapped from bundle: %s" % action_bundle_path)
		return false
	_resample_action_bones_if_needed()

	if print_summary:
		print(
			"Kawaii action loaded: action=%s source_hz=%.1f runtime_hz=%.1f frames=%d mapped_bones=%d skeleton=%s"
			% [_action_name, _source_sample_rate, _sample_rate, _frame_count, _action_bones.size(), _skeleton.get_path()]
		)
	return true


func _load_action_entries() -> void:
	_action_entries.clear()
	var dir := DirAccess.open(action_bundle_dir)
	if dir == null:
		push_warning("Kawaii action bundle dir missing: %s" % action_bundle_dir)
		return

	dir.list_dir_begin()
	var file_name := dir.get_next()
	while not file_name.is_empty():
		if not dir.current_is_dir() and file_name.ends_with("_action_bundle.json"):
			var action_name := file_name.replace("_action_bundle.json", "")
			if not _is_action_allowed(action_name):
				file_name = dir.get_next()
				continue
			var path := "%s/%s" % [action_bundle_dir.trim_suffix("/"), file_name]
			_action_entries.append({
				"name": action_name,
				"path": path,
			})
		file_name = dir.get_next()
	dir.list_dir_end()
	_action_entries.sort_custom(Callable(self, "_sort_action_entries"))


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


func _sort_action_entries(left: Dictionary, right: Dictionary) -> bool:
	return String(left.get("name", "")) < String(right.get("name", ""))


func _select_initial_action_path() -> void:
	if _action_entries.is_empty():
		return
	var normalized_path := action_bundle_path.replace("\\", "/")
	var current_file := normalized_path.get_file()
	for i in range(_action_entries.size()):
		var entry_path := String(_action_entries[i].get("path", ""))
		if entry_path == normalized_path or entry_path.get_file() == current_file:
			_action_index = i
			action_bundle_path = entry_path
			return
	_action_index = 0
	if action_bundle_path.is_empty() or not FileAccess.file_exists(action_bundle_path):
		action_bundle_path = String(_action_entries[0].get("path", action_bundle_path))


func _select_initial_startup_idle_path() -> void:
	if _action_entries.is_empty():
		return
	var idle_action := _pick_startup_idle_action_name()
	var idle_index := _find_action_index_by_name(idle_action)
	if idle_index < 0:
		return
	_action_index = idle_index
	action_bundle_path = String(_action_entries[idle_index].get("path", action_bundle_path))


func _parse_imported_rest_pose(rest_pose_variant: Variant) -> Dictionary:
	var output := {}
	if typeof(rest_pose_variant) != TYPE_DICTIONARY:
		return output
	var bones = (rest_pose_variant as Dictionary).get("bones", [])
	if typeof(bones) != TYPE_ARRAY:
		return output
	for bone_variant in bones:
		if typeof(bone_variant) != TYPE_DICTIONARY:
			continue
		var bone: Dictionary = bone_variant
		var bone_name := str(bone.get("name", ""))
		if bone_name.is_empty():
			continue
		output[bone_name] = {
			"position": _vector3_from_array(bone.get("local_position", []), Vector3.ZERO),
			"rotation": _quaternion_from_array(bone.get("local_rotation", []), Quaternion.IDENTITY),
			"scale": _vector3_from_array(bone.get("local_scale", []), Vector3.ONE),
			"path": str(bone.get("path", "")),
			"parent": str(bone.get("parent", "")),
		}
	return output


func _parse_action_bones(actions_variant: Variant) -> Array[Dictionary]:
	var output: Array[Dictionary] = []
	if typeof(actions_variant) != TYPE_ARRAY or (actions_variant as Array).is_empty():
		return output

	var action: Dictionary = (actions_variant as Array)[0]
	var bones = action.get("bones", [])
	if typeof(bones) != TYPE_ARRAY:
		return output

	var unresolved_changed := 0
	for bone_variant in bones:
		if typeof(bone_variant) != TYPE_DICTIONARY:
			continue
		var bone: Dictionary = bone_variant
		var imported_name := str(bone.get("name", ""))
		var runtime_name := _resolve_runtime_bone_name(imported_name)
		if runtime_name.is_empty() or not _runtime_rest_pose_map.has(runtime_name):
			unresolved_changed += 1
			continue
		if not _imported_rest_pose_map.has(imported_name):
			unresolved_changed += 1
			continue

		var positions := _parse_vector3_frames(bone.get("local_positions", []))
		var rotations := _parse_quaternion_frames(bone.get("local_rotations", []))
		if rotations.is_empty():
			continue
		output.append({
			"imported_name": imported_name,
			"runtime_name": runtime_name,
			"runtime_index": int(_bone_lookup[runtime_name]),
			"positions": positions,
			"rotations": rotations,
			"translation_scale": _estimate_translation_scale(
				(_runtime_rest_pose_map[runtime_name]["position"] as Vector3),
				(_imported_rest_pose_map[imported_name]["position"] as Vector3)
			),
		})

	if print_summary:
		print("Kawaii action bone mapping: mapped=%d unresolved=%d" % [output.size(), unresolved_changed])
	return output


func _resample_action_bones_if_needed() -> void:
	if not runtime_resample_actions:
		return
	var target_rate := maxf(target_action_sample_rate, _sample_rate)
	if target_rate <= _sample_rate + 0.01:
		return
	if _length_sec <= 0.0 or _action_bones.is_empty():
		return

	var source_rate := _sample_rate
	var target_frame_count := maxi(2, ceili(_length_sec * target_rate) + 1)
	for i in range(_action_bones.size()):
		var action_bone: Dictionary = _action_bones[i]
		var rotations: Array = action_bone["rotations"]
		if not rotations.is_empty():
			action_bone["rotations"] = _resample_quaternion_frames_to_rate(
				rotations,
				source_rate,
				target_rate,
				target_frame_count
			)
		var positions: Array = action_bone["positions"]
		if not positions.is_empty():
			action_bone["positions"] = _resample_vector3_frames_to_rate(
				positions,
				source_rate,
				target_rate,
				target_frame_count
			)
		_action_bones[i] = action_bone

	_sample_rate = target_rate
	_frame_count = target_frame_count
	if print_summary:
		print("Kawaii action runtime resample: %.1f Hz -> %.1f Hz, frames=%d" % [
			source_rate,
			_sample_rate,
			_frame_count,
		])


func _resample_quaternion_frames_to_rate(frames: Array, source_rate: float, target_rate: float, target_frame_count: int) -> Array[Quaternion]:
	var output: Array[Quaternion] = []
	output.resize(target_frame_count)
	for frame_index in range(target_frame_count):
		var sample_time := float(frame_index) / target_rate
		output[frame_index] = _sample_quaternion_frames(frames, source_rate, sample_time)
	return output


func _resample_vector3_frames_to_rate(frames: Array, source_rate: float, target_rate: float, target_frame_count: int) -> Array[Vector3]:
	var output: Array[Vector3] = []
	output.resize(target_frame_count)
	for frame_index in range(target_frame_count):
		var sample_time := float(frame_index) / target_rate
		output[frame_index] = _sample_vector3_frames(frames, source_rate, sample_time)
	return output


func _sample_quaternion_frames(frames: Array, source_rate: float, time_sec: float) -> Quaternion:
	if frames.is_empty():
		return Quaternion.IDENTITY
	var raw_frame := clampf(time_sec * source_rate, 0.0, maxf(float(frames.size() - 1), 0.0))
	var frame_a := int(floor(raw_frame))
	var frame_b := mini(frame_a + 1, frames.size() - 1)
	var weight := raw_frame - float(frame_a)
	var rot_a: Quaternion = frames[frame_a]
	var rot_b: Quaternion = frames[frame_b]
	return rot_a.slerp(rot_b, weight).normalized()


func _sample_vector3_frames(frames: Array, source_rate: float, time_sec: float) -> Vector3:
	if frames.is_empty():
		return Vector3.ZERO
	var raw_frame := clampf(time_sec * source_rate, 0.0, maxf(float(frames.size() - 1), 0.0))
	var frame_a := int(floor(raw_frame))
	var frame_b := mini(frame_a + 1, frames.size() - 1)
	var weight := raw_frame - float(frame_a)
	var pos_a: Vector3 = frames[frame_a]
	var pos_b: Vector3 = frames[frame_b]
	return pos_a.lerp(pos_b, weight)


func _apply_action_at_time(time_sec: float) -> void:
	if _sample_rate <= 0.0 or _frame_count <= 0:
		return
	var raw_frame := clampf(time_sec * _sample_rate, 0.0, maxf(float(_frame_count - 1), 0.0))
	var frame_a := int(floor(raw_frame))
	var frame_b := mini(frame_a + 1, _frame_count - 1)
	var weight := raw_frame - float(frame_a)

	for action_bone in _action_bones:
		var imported_name := String(action_bone["imported_name"])
		var runtime_name := String(action_bone["runtime_name"])
		var bone_idx := int(action_bone["runtime_index"])
		var rotations: Array = action_bone["rotations"]
		var positions: Array = action_bone["positions"]
		if frame_a >= rotations.size():
			continue

		var imported_rest: Dictionary = _imported_rest_pose_map[imported_name]
		var runtime_rest: Dictionary = _runtime_rest_pose_map[runtime_name]
		var rot_a: Quaternion = rotations[frame_a]
		var rot_b: Quaternion = rotations[mini(frame_b, rotations.size() - 1)]
		var pose_rotation := rot_a.slerp(rot_b, weight).normalized()
		var delta_rotation := _compose_runtime_pose_delta(imported_rest["rotation"], pose_rotation)
		var target_rotation := ((runtime_rest["rotation"] as Quaternion) * delta_rotation).normalized()

		var target_position: Vector3 = runtime_rest["position"]
		if apply_root_translation and _should_apply_pose_translation(imported_name, runtime_name) and not positions.is_empty():
			var pos_a: Vector3 = positions[mini(frame_a, positions.size() - 1)]
			var pos_b: Vector3 = positions[mini(frame_b, positions.size() - 1)]
			var imported_position := pos_a.lerp(pos_b, weight)
			var imported_rest_position: Vector3 = imported_rest["position"]
			var delta_position := imported_position - imported_rest_position
			target_position = (runtime_rest["position"] as Vector3) + delta_position * float(action_bone["translation_scale"])

		if _transition_active and _transition_start_pose.has(runtime_name):
			var start_pose: Dictionary = _transition_start_pose[runtime_name]
			var transition_weight := _get_action_transition_weight()
			target_position = (start_pose["position"] as Vector3).lerp(target_position, transition_weight)
			target_rotation = (start_pose["rotation"] as Quaternion).slerp(target_rotation, transition_weight).normalized()
			var target_scale: Vector3 = runtime_rest["scale"]
			target_scale = (start_pose["scale"] as Vector3).lerp(target_scale, transition_weight)
			_skeleton.set_bone_pose_position(bone_idx, target_position)
			_skeleton.set_bone_pose_rotation(bone_idx, target_rotation)
			_skeleton.set_bone_pose_scale(bone_idx, target_scale)
			continue

		_skeleton.set_bone_pose_position(bone_idx, target_position)
		_skeleton.set_bone_pose_rotation(bone_idx, target_rotation)
		_skeleton.set_bone_pose_scale(bone_idx, runtime_rest["scale"])


func _capture_current_skeleton_pose() -> Dictionary:
	var output := {}
	if _skeleton == null:
		return output
	for bone_idx in range(_skeleton.get_bone_count()):
		var bone_name := _skeleton.get_bone_name(bone_idx)
		output[bone_name] = {
			"position": _skeleton.get_bone_pose_position(bone_idx),
			"rotation": _skeleton.get_bone_pose_rotation(bone_idx),
			"scale": _skeleton.get_bone_pose_scale(bone_idx),
		}
	return output


func _start_action_transition(start_pose: Dictionary) -> void:
	_transition_start_pose.clear()
	_transition_active = false
	_transition_elapsed = 0.0
	_transition_duration = maxf(action_transition_sec, 0.0)
	if _transition_duration <= 0.0 or start_pose.is_empty():
		_reset_runtime_pose()
		return
	_transition_start_pose = start_pose.duplicate(true)
	_transition_active = true


func _get_action_transition_weight() -> float:
	if not _transition_active or _transition_duration <= 0.0:
		return 1.0
	var t := clampf(_transition_elapsed / _transition_duration, 0.0, 1.0)
	return t * t * (3.0 - 2.0 * t)


func _reset_runtime_pose() -> void:
	if _skeleton == null:
		return
	for bone_name_variant in _runtime_rest_pose_map.keys():
		var bone_name := String(bone_name_variant)
		if not _bone_lookup.has(bone_name):
			continue
		var bone_idx: int = _bone_lookup[bone_name]
		var rest: Dictionary = _runtime_rest_pose_map[bone_name]
		_skeleton.set_bone_pose_position(bone_idx, rest["position"])
		_skeleton.set_bone_pose_rotation(bone_idx, rest["rotation"])
		_skeleton.set_bone_pose_scale(bone_idx, rest["scale"])


func _capture_runtime_rest_pose() -> void:
	_runtime_rest_pose_map.clear()
	if _skeleton == null:
		return
	for bone_idx in range(_skeleton.get_bone_count()):
		var bone_name := _skeleton.get_bone_name(bone_idx)
		_runtime_rest_pose_map[bone_name] = {
			"position": _skeleton.get_bone_pose_position(bone_idx),
			"rotation": _skeleton.get_bone_pose_rotation(bone_idx),
			"scale": _skeleton.get_bone_pose_scale(bone_idx),
		}


func _compose_runtime_pose_delta(imported_rest_rotation: Quaternion, imported_pose_rotation: Quaternion) -> Quaternion:
	var rest_rotation := _convert_imported_rotation_to_runtime(imported_rest_rotation)
	var pose_rotation := _convert_imported_rotation_to_runtime(imported_pose_rotation)
	return (rest_rotation.inverse() * pose_rotation).normalized()


func _convert_imported_rotation_to_runtime(rotation: Quaternion) -> Quaternion:
	match rotation_conversion:
		"raw":
			return rotation.normalized()
		"handed_z":
			return Quaternion(-rotation.x, -rotation.y, rotation.z, rotation.w).normalized()
		"handed_y":
			return Quaternion(-rotation.x, rotation.y, -rotation.z, rotation.w).normalized()
		_:
			return Quaternion(rotation.x, -rotation.y, -rotation.z, rotation.w).normalized()


func _resolve_runtime_bone_name(imported_bone_name: String) -> String:
	if _runtime_rest_pose_map.has(imported_bone_name):
		return imported_bone_name
	var normalized := _normalize_bone_name_for_lookup(imported_bone_name)
	if normalized.is_empty():
		return ""
	return String(_bone_alias_lookup.get(normalized, ""))


func _normalize_bone_name_for_lookup(bone_name: String) -> String:
	return bone_name.to_lower().replace(" ", "").replace("_", "").replace("-", "").replace(".", "")


func _should_apply_pose_translation(imported_bone_name: String, runtime_bone_name: String) -> bool:
	var joined := ("%s %s" % [imported_bone_name, runtime_bone_name]).to_lower()
	return (
		joined.contains("hips")
		or joined.contains("pelvis")
		or joined.contains("root")
		or joined.contains("center")
		or joined.contains("groove")
		or joined.contains("センター")
		or joined.contains("グルーブ")
		or joined.contains("全ての親")
		or joined.contains("腰")
	)


func _estimate_translation_scale(runtime_rest_position: Vector3, imported_rest_position: Vector3) -> float:
	var runtime_length := runtime_rest_position.length()
	var imported_length := imported_rest_position.length()
	if imported_length <= 0.000001 or runtime_length <= 0.000001:
		return 100.0
	return clampf(runtime_length / imported_length, 0.01, 500.0)


func _parse_vector3_frames(value: Variant) -> Array[Vector3]:
	var output: Array[Vector3] = []
	if typeof(value) != TYPE_ARRAY:
		return output
	for item in value:
		output.append(_vector3_from_array(item, Vector3.ZERO))
	return output


func _parse_quaternion_frames(value: Variant) -> Array[Quaternion]:
	var output: Array[Quaternion] = []
	if typeof(value) != TYPE_ARRAY:
		return output
	for item in value:
		var rotation := _quaternion_from_array(item, Quaternion.IDENTITY)
		if not output.is_empty() and output[output.size() - 1].dot(rotation) < 0.0:
			rotation = Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w)
		output.append(rotation.normalized())
	return output


func _vector3_from_array(value: Variant, fallback: Vector3) -> Vector3:
	if typeof(value) != TYPE_ARRAY or (value as Array).size() < 3:
		return fallback
	return Vector3(float(value[0]), float(value[1]), float(value[2]))


func _quaternion_from_array(value: Variant, fallback: Quaternion) -> Quaternion:
	if typeof(value) != TYPE_ARRAY or (value as Array).size() < 4:
		return fallback
	return Quaternion(float(value[0]), float(value[1]), float(value[2]), float(value[3])).normalized()


func _load_json_dict(path: String) -> Dictionary:
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


func _choose_skeleton(skeletons: Array[Skeleton3D]) -> Skeleton3D:
	var best: Skeleton3D = null
	var best_score := -1
	for candidate in skeletons:
		var score := candidate.get_bone_count()
		if score > best_score:
			best = candidate
			best_score = score
	return best


func _collect_skeletons(node: Node, out: Array[Skeleton3D]) -> void:
	if node is Skeleton3D:
		out.append(node)
	for child in node.get_children():
		_collect_skeletons(child, out)


func _stop_animation_players(node: Node) -> void:
	if node is AnimationPlayer:
		(node as AnimationPlayer).stop()
	for child in node.get_children():
		_stop_animation_players(child)


func _fit_model_to_view(model_root: Node3D) -> void:
	var meshes: Array[MeshInstance3D] = []
	_collect_meshes(model_root, meshes)
	var bounds := _merged_mesh_bounds(meshes)
	if bounds.size == Vector3.ZERO:
		return

	var height := maxf(bounds.size.y, 0.001)
	var fit_scale := target_height / height
	model_root.scale *= fit_scale
	model_root.force_update_transform()
	meshes.clear()
	_collect_meshes(model_root, meshes)
	bounds = _merged_mesh_bounds(meshes)
	if bounds.size == Vector3.ZERO:
		return

	var center := bounds.get_center()
	model_root.global_position += Vector3(
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


func _update_status(force_print: bool = false) -> void:
	var action_count := _action_entries.size()
	var action_index_text := "%d/%d" % [_action_index + 1, action_count] if action_count > 0 and _action_index >= 0 else "single"
	var status := "%s | %s | %s | %.2fs / %.2fs | %.0f->%.0f Hz | bones %d" % [
		action_index_text,
		_action_name,
		"playing" if _playing else "paused",
		_elapsed_sec,
		_length_sec,
		_source_sample_rate,
		_sample_rate,
		_action_bones.size(),
	]
	if _status_label != null:
		_status_label.text = status + "\nLeft/Right switch action  Space pause/play  R reset"
	if force_print or status != _last_status:
		_last_status = status


func _fail(reason: String) -> void:
	push_error(reason)
	if _status_label != null:
		_status_label.text = reason
	action_load_failed.emit(reason)
