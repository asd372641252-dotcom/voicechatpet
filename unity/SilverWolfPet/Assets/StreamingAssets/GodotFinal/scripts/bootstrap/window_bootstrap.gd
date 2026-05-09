extends Node

@export var window_size := Vector2i(512, 720)
@export var fixed_size := true
@export var ctrl_wheel_resize := true
@export var window_resize_step := 72
@export var min_window_size := Vector2i(256, 360)
@export var max_window_size := Vector2i(3072, 4320)
@export_enum("Disabled:0", "2x:1", "4x:2", "8x:3") var msaa_3d := 3
@export_enum("Disabled:0", "2x:1", "4x:2", "8x:3") var msaa_2d := 0
@export var lighting_controller_path := NodePath("PetRoot/LightingController")
@export var material_toggle_controller_path := NodePath("PetRoot/AnimeRenderController")
@export var anime_render_controller_path := NodePath("PetRoot/AnimeRenderController")
@export var hit_mask_polygon_path := NodePath("HitMaskPolygon")
@export var win_click_controller_path := NodePath("WinClickThroughController")
@export var free_camera_path := NodePath("PetRoot/Camera3D")
@export var disable_free_camera_input := true
@export var right_click_drag_threshold := 8.0
@export var context_menu_toggle_debounce_ms := 220
@export var context_menu_min_size := Vector2(280.0, 400.0)
@export var context_menu_button_size := Vector2(248.0, 36.0)
@export var context_menu_font_size := 15
@export var context_menu_section_font_size := 13
@export var context_menu_title_font_size := 17

var _right_pressed := false
var _right_press_position := Vector2.ZERO
var _last_context_menu_open_msec := 0
var _last_context_menu_close_msec := 0

var _context_layer: CanvasLayer
var _context_panel: PanelContainer
var _context_title: Label
var _context_tween: Tween
var _hit_mask_debug_button: Button
var _glasses_button: Button
var _wings_button: Button
var _preset_soft_button: Button
var _preset_cel_button: Button
var _preset_flat_button: Button
var _light_system_button: Button
var _light_soft_button: Button
var _light_bright_button: Button
var _light_dim_button: Button
var _hit_mask_debug_visible := false

# ---- menu color palette ----
const MENU_BG := Color(0.086, 0.094, 0.11, 0.965)
const MENU_BORDER := Color(1.0, 1.0, 1.0, 0.12)
const MENU_HEADER_BG := Color(1.0, 1.0, 1.0, 0.04)
const MENU_ACCENT := Color(0.4, 0.65, 1.0, 1.0)        # blue accent
const MENU_ACCENT_DIM := Color(0.4, 0.65, 1.0, 0.35)
const MENU_TEXT := Color(0.92, 0.94, 0.98, 1.0)
const MENU_TEXT_DIM := Color(0.55, 0.58, 0.64, 1.0)
const MENU_BTN_HOVER := Color(1.0, 1.0, 1.0, 0.07)
const MENU_BTN_ACTIVE := Color(1.0, 1.0, 1.0, 0.05)
const MENU_BTN_PRESSED := Color(1.0, 1.0, 1.0, 0.03)
const MENU_DIVIDER := Color(1.0, 1.0, 1.0, 0.06)
const MENU_CHECK_ON := Color(0.4, 0.65, 1.0, 1.0)
const MENU_CHECK_OFF := Color(0.35, 0.37, 0.42, 1.0)
const MENU_CLOSE_HOVER := Color(0.95, 0.3, 0.25, 0.85)

const LIGHT_FOLLOW_SYSTEM := 0
const LIGHT_SOFT := 1
const LIGHT_BRIGHT := 2
const LIGHT_DIM := 3


func _enter_tree() -> void:
	_apply_desktop_window_flags()
	_apply_render_quality()


func _ready() -> void:
	_apply_desktop_window_flags()
	_apply_render_quality()
	_apply_desktop_input_modes()
	_apply_hit_mask_debug_visibility()
	_ensure_context_menu()


func _input(event: InputEvent) -> void:
	if _handle_window_resize_input(event):
		return

	if _handle_context_menu_input(event):
		return


func _apply_desktop_window_flags() -> void:
	var root_window := get_tree().root
	var window := get_window()
	window.transparent = true
	window.transparent_bg = true
	window.borderless = true
	window.always_on_top = true
	window.size = window_size
	root_window.transparent_bg = true
	get_viewport().transparent_bg = true
	if RenderingServer.has_method("viewport_set_transparent_background"):
		RenderingServer.call("viewport_set_transparent_background", get_viewport().get_viewport_rid(), true)
	RenderingServer.set_default_clear_color(Color(0.0, 0.0, 0.0, 0.0))

	DisplayServer.window_set_mode(DisplayServer.WINDOW_MODE_WINDOWED)
	DisplayServer.window_set_size(window_size)
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_TRANSPARENT, true)
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_BORDERLESS, true)
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_ALWAYS_ON_TOP, true)

	if fixed_size:
		_apply_fixed_size_constraints(window_size)
		DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_RESIZE_DISABLED, true)


func _apply_render_quality() -> void:
	var viewport := get_viewport()
	viewport.msaa_3d = msaa_3d
	viewport.msaa_2d = msaa_2d


func _ensure_context_menu() -> void:
	if _context_panel != null:
		return

	_context_layer = CanvasLayer.new()
	_context_layer.name = "DesktopPetContextLayer"
	_context_layer.layer = 100
	add_child(_context_layer)

	# -- panel container --
	_context_panel = PanelContainer.new()
	_context_panel.name = "DesktopPetContextPanel"
	_context_panel.visible = false
	_context_panel.custom_minimum_size = context_menu_min_size
	_context_panel.modulate.a = 0.0  # start hidden for fade-in
	var panel_style := _make_panel_stylebox()
	_context_panel.add_theme_stylebox_override("panel", panel_style)
	_context_layer.add_child(_context_panel)

	# -- vertical layout --
	var vbox := VBoxContainer.new()
	vbox.add_theme_constant_override("separation", 0)
	_context_panel.add_child(vbox)

	# == HEADER ==
	var header := _make_menu_header()
	vbox.add_child(header)

	# == BODY (scrollable content with padding) ==
	var body_margin := MarginContainer.new()
	body_margin.add_theme_constant_override("margin_left", 14)
	body_margin.add_theme_constant_override("margin_top", 6)
	body_margin.add_theme_constant_override("margin_right", 14)
	body_margin.add_theme_constant_override("margin_bottom", 12)
	vbox.add_child(body_margin)

	var body := VBoxContainer.new()
	body.add_theme_constant_override("separation", 2)
	body_margin.add_child(body)

	# -- 显示 section --
	_add_menu_section_label(body, "显示")
	_hit_mask_debug_button = _make_menu_toggle("命中区域")
	_hit_mask_debug_button.pressed.connect(_toggle_hit_mask_debug)
	body.add_child(_hit_mask_debug_button)

	_glasses_button = _make_menu_toggle("眼镜")
	_glasses_button.pressed.connect(_toggle_glasses_visible)
	body.add_child(_glasses_button)

	_wings_button = _make_menu_toggle("翅膀")
	_wings_button.pressed.connect(_toggle_wings_visible)
	body.add_child(_wings_button)

	_add_menu_divider(body)

	# -- 渲染风格 section --
	_add_menu_section_label(body, "渲染风格")
	_preset_soft_button = _make_menu_radio("柔光")
	_preset_soft_button.pressed.connect(func() -> void: _set_anime_render_preset("DesktopPetSoft"))
	body.add_child(_preset_soft_button)

	_preset_cel_button = _make_menu_radio("赛璐璐")
	_preset_cel_button.pressed.connect(func() -> void: _set_anime_render_preset("CelAnime"))
	body.add_child(_preset_cel_button)

	_preset_flat_button = _make_menu_radio("平面色块")
	_preset_flat_button.pressed.connect(func() -> void: _set_anime_render_preset("FlatLive2DLike"))
	body.add_child(_preset_flat_button)

	_add_menu_divider(body)

	# -- 光照 section --
	_add_menu_section_label(body, "光照")
	_light_system_button = _make_menu_radio("跟随系统")
	_light_system_button.pressed.connect(func() -> void: _set_light_mode(LIGHT_FOLLOW_SYSTEM))
	body.add_child(_light_system_button)

	_light_soft_button = _make_menu_radio("柔和")
	_light_soft_button.pressed.connect(func() -> void: _set_light_mode(LIGHT_SOFT))
	body.add_child(_light_soft_button)

	_light_bright_button = _make_menu_radio("明亮")
	_light_bright_button.pressed.connect(func() -> void: _set_light_mode(LIGHT_BRIGHT))
	body.add_child(_light_bright_button)

	_light_dim_button = _make_menu_radio("偏暗")
	_light_dim_button.pressed.connect(func() -> void: _set_light_mode(LIGHT_DIM))
	body.add_child(_light_dim_button)

	_update_context_menu_checks()


# ---- menu builder helpers ----

func _make_panel_stylebox() -> StyleBoxFlat:
	var s := StyleBoxFlat.new()
	s.bg_color = MENU_BG
	s.border_color = MENU_BORDER
	s.set_border_width_all(1)
	s.set_corner_radius_all(10)
	s.shadow_color = Color(0, 0, 0, 0.4)
	s.shadow_size = 18
	s.shadow_offset = Vector2(0, 4)
	return s


func _make_menu_header() -> PanelContainer:
	# wrap in PanelContainer so we can use "panel" stylebox for background
	var wrapper := PanelContainer.new()
	wrapper.custom_minimum_size = Vector2(0, 44)
	var header_style := StyleBoxFlat.new()
	header_style.bg_color = MENU_HEADER_BG
	header_style.corner_radius_top_left = 10
	header_style.corner_radius_top_right = 10
	header_style.corner_radius_bottom_left = 0
	header_style.corner_radius_bottom_right = 0
	wrapper.add_theme_stylebox_override("panel", header_style)

	var header := HBoxContainer.new()
	header.custom_minimum_size = Vector2(0, 44)
	wrapper.add_child(header)

	# left indent
	var left_pad := Control.new()
	left_pad.custom_minimum_size = Vector2(14, 0)
	header.add_child(left_pad)

	# title
	_context_title = Label.new()
	_context_title.text = "桌宠设置"
	_context_title.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	_context_title.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_context_title.add_theme_color_override("font_color", MENU_TEXT)
	_context_title.add_theme_font_size_override("font_size", context_menu_title_font_size)
	header.add_child(_context_title)

	# close button
	var close_btn := Button.new()
	close_btn.text = "✕"
	close_btn.flat = true
	close_btn.alignment = HORIZONTAL_ALIGNMENT_CENTER
	close_btn.custom_minimum_size = Vector2(36, 32)
	close_btn.add_theme_font_size_override("font_size", 14)
	close_btn.add_theme_color_override("font_color", MENU_TEXT_DIM)
	close_btn.add_theme_color_override("font_hover_color", MENU_CLOSE_HOVER)
	close_btn.add_theme_color_override("font_pressed_color", MENU_CLOSE_HOVER)
	_style_button_flat(close_btn, MENU_CLOSE_HOVER)
	close_btn.pressed.connect(_hide_context_menu)
	header.add_child(close_btn)

	# right indent
	var right_pad := Control.new()
	right_pad.custom_minimum_size = Vector2(6, 0)
	header.add_child(right_pad)

	return wrapper


func _add_menu_section_label(parent: Control, label_text: String) -> void:
	var lbl := Label.new()
	lbl.text = label_text
	lbl.add_theme_color_override("font_color", MENU_TEXT_DIM)
	lbl.add_theme_font_size_override("font_size", context_menu_section_font_size)
	lbl.custom_minimum_size = Vector2(0, 24)
	lbl.vertical_alignment = VERTICAL_ALIGNMENT_BOTTOM
	parent.add_child(lbl)


func _add_menu_divider(parent: Control) -> void:
	# use a thin colored rect as divider instead of HSeparator
	var div := ColorRect.new()
	div.color = MENU_DIVIDER
	div.custom_minimum_size = Vector2(0, 1)
	parent.add_child(div)

	var spacer := Control.new()
	spacer.custom_minimum_size = Vector2(0, 6)
	parent.add_child(spacer)


func _make_menu_toggle(label: String) -> Button:
	var btn := Button.new()
	btn.text = "  %s" % label
	btn.alignment = HORIZONTAL_ALIGNMENT_LEFT
	btn.focus_mode = Control.FOCUS_NONE
	btn.custom_minimum_size = context_menu_button_size
	btn.add_theme_font_size_override("font_size", context_menu_font_size)
	btn.add_theme_color_override("font_color", MENU_TEXT)
	btn.icon_alignment = HORIZONTAL_ALIGNMENT_LEFT
	_style_button_list_item(btn)
	return btn


func _make_menu_radio(label: String) -> Button:
	var btn := Button.new()
	btn.text = "  %s" % label
	btn.alignment = HORIZONTAL_ALIGNMENT_LEFT
	btn.focus_mode = Control.FOCUS_NONE
	btn.custom_minimum_size = context_menu_button_size
	btn.add_theme_font_size_override("font_size", context_menu_font_size)
	btn.add_theme_color_override("font_color", MENU_TEXT)
	_style_button_list_item(btn)
	return btn


func _style_button_list_item(btn: Button) -> void:
	# normal state
	var normal := StyleBoxFlat.new()
	normal.bg_color = Color.TRANSPARENT
	normal.set_corner_radius_all(6)
	btn.add_theme_stylebox_override("normal", normal)

	# hover
	var hover := StyleBoxFlat.new()
	hover.bg_color = MENU_BTN_HOVER
	hover.set_corner_radius_all(6)
	btn.add_theme_stylebox_override("hover", hover)

	# pressed
	var pressed := StyleBoxFlat.new()
	pressed.bg_color = MENU_BTN_PRESSED
	pressed.set_corner_radius_all(6)
	btn.add_theme_stylebox_override("pressed", pressed)

	# focus (same as normal since FOCUS_NONE)
	btn.add_theme_stylebox_override("focus", normal)


func _style_button_flat(btn: Button, hover_color: Color) -> void:
	var trans := StyleBoxFlat.new()
	trans.bg_color = Color.TRANSPARENT
	trans.set_corner_radius_all(4)
	btn.add_theme_stylebox_override("normal", trans)

	var hov := StyleBoxFlat.new()
	hov.bg_color = Color(hover_color.r, hover_color.g, hover_color.b, 0.15)
	hov.set_corner_radius_all(4)
	btn.add_theme_stylebox_override("hover", hov)


func _add_stylebox_bg(ctrl: Control, color: Color, tl: int, tr: int, bl: int, br: int) -> void:
	var s := StyleBoxFlat.new()
	s.bg_color = color
	s.corner_radius_top_left = tl
	s.corner_radius_top_right = tr
	s.corner_radius_bottom_left = bl
	s.corner_radius_bottom_right = br
	ctrl.add_theme_stylebox_override("panel", s)


func _handle_context_menu_input(event: InputEvent) -> bool:
	if event is InputEventKey:
		var key_event := event as InputEventKey
		if key_event.pressed and key_event.keycode == KEY_ESCAPE and _context_panel != null and _context_panel.visible:
			_hide_context_menu()
			get_viewport().set_input_as_handled()
			return true

	if event is InputEventMouseButton:
		if _uses_native_context_menu():
			return false

		var mouse_button := event as InputEventMouseButton
		if mouse_button.button_index != MOUSE_BUTTON_RIGHT:
			return false

		if mouse_button.pressed:
			_right_pressed = true
			_right_press_position = mouse_button.position
			get_viewport().set_input_as_handled()
			return true

		if _right_pressed:
			_right_pressed = false
			if _right_press_position.distance_to(mouse_button.position) <= right_click_drag_threshold:
				_toggle_context_menu(mouse_button.position)
				get_viewport().set_input_as_handled()
				return true
			get_viewport().set_input_as_handled()
			return true
		return false

	return false


func _uses_native_context_menu() -> bool:
	var controller := get_node_or_null(win_click_controller_path)
	return controller != null and controller.has_method("SetClickThrough")


func _show_context_menu(position: Vector2) -> void:
	_ensure_context_menu()
	_update_context_menu_checks()
	_context_panel.position = _clamp_context_menu_position(position)
	_context_panel.show()
	_context_panel.move_to_front()

	# fade in
	if _context_tween != null and _context_tween.is_running():
		_context_tween.kill()
	_context_tween = create_tween().set_ease(Tween.EASE_OUT).set_trans(Tween.TRANS_CUBIC)
	_context_tween.tween_property(_context_panel, "modulate:a", 1.0, 0.18)

	_last_context_menu_open_msec = Time.get_ticks_msec()
	_set_window_interaction_lock(false)
	_set_context_menu_interactive_rect(true)
	call_deferred("_refresh_context_menu_interactive_rect")


func _toggle_context_menu(position: Vector2) -> void:
	var now_msec := Time.get_ticks_msec()
	if _context_panel != null and _context_panel.visible:
		if now_msec - _last_context_menu_open_msec < context_menu_toggle_debounce_ms:
			return
		_hide_context_menu()
		return
	if now_msec - _last_context_menu_close_msec < context_menu_toggle_debounce_ms:
		return
	_show_context_menu(position)


func _hide_context_menu() -> void:
	if _context_panel == null or not _context_panel.visible:
		return

	_set_context_menu_interactive_rect(false)
	_set_window_interaction_lock(false)
	_last_context_menu_close_msec = Time.get_ticks_msec()

	if _context_tween != null and _context_tween.is_running():
		_context_tween.kill()
	_context_tween = create_tween().set_ease(Tween.EASE_IN).set_trans(Tween.TRANS_CUBIC)
	_context_tween.tween_property(_context_panel, "modulate:a", 0.0, 0.12)
	_context_tween.tween_callback(func():
		if _context_panel != null:
			_context_panel.hide()
	)


func show_context_menu_from_native(position: Vector2) -> void:
	_toggle_context_menu(position)


func _clamp_context_menu_position(position: Vector2) -> Vector2:
	var viewport_size := get_viewport().get_visible_rect().size
	var panel_size := _context_panel.size
	if panel_size.x <= 1.0 or panel_size.y <= 1.0:
		panel_size = _context_panel.custom_minimum_size

	return Vector2(
		clampf(position.x, 8.0, maxf(8.0, viewport_size.x - panel_size.x - 8.0)),
		clampf(position.y, 8.0, maxf(8.0, viewport_size.y - panel_size.y - 8.0))
	)


func _update_context_menu_checks() -> void:
	if _hit_mask_debug_button == null:
		return

	_set_toggle_button_state(_hit_mask_debug_button, _hit_mask_debug_visible)
	if _glasses_button != null:
		_set_toggle_button_state(_glasses_button, _get_material_toggle_visible("get_glasses_visible", false))
	if _wings_button != null:
		_set_toggle_button_state(_wings_button, _get_material_toggle_visible("get_wings_visible", false))

	var current_preset := _get_anime_render_preset()
	_set_radio_button_state(_preset_soft_button, current_preset == "DesktopPetSoft")
	_set_radio_button_state(_preset_cel_button, current_preset == "CelAnime")
	_set_radio_button_state(_preset_flat_button, current_preset == "FlatLive2DLike")

	var light_mode := _get_light_mode()
	_set_radio_button_state(_light_system_button, light_mode == LIGHT_FOLLOW_SYSTEM)
	_set_radio_button_state(_light_soft_button, light_mode == LIGHT_SOFT)
	_set_radio_button_state(_light_bright_button, light_mode == LIGHT_BRIGHT)
	_set_radio_button_state(_light_dim_button, light_mode == LIGHT_DIM)


func _set_toggle_button_state(btn: Button, active: bool) -> void:
	if active:
		btn.text = "✔ %s" % btn.text.trim_prefix("  ").trim_prefix("✔ ").trim_prefix("◯ ")
		btn.add_theme_color_override("font_color", MENU_ACCENT)
	else:
		btn.text = "  %s" % btn.text.trim_prefix("  ").trim_prefix("✔ ").trim_prefix("◯ ")
		btn.add_theme_color_override("font_color", MENU_TEXT)


func _set_radio_button_state(btn: Button, active: bool) -> void:
	if active:
		btn.text = "● %s" % btn.text.trim_prefix("  ").trim_prefix("● ").trim_prefix("○ ")
		btn.add_theme_color_override("font_color", MENU_ACCENT)
	else:
		btn.text = "  %s" % btn.text.trim_prefix("  ").trim_prefix("● ").trim_prefix("○ ")
		btn.add_theme_color_override("font_color", MENU_TEXT)


func _toggle_hit_mask_debug() -> void:
	_hit_mask_debug_visible = not _hit_mask_debug_visible
	_apply_hit_mask_debug_visibility()
	_update_context_menu_checks()


func _apply_hit_mask_debug_visibility() -> void:
	var polygon := get_node_or_null(hit_mask_polygon_path)
	if polygon is Polygon2D:
		(polygon as Polygon2D).visible = _hit_mask_debug_visible

	var controller := get_node_or_null(win_click_controller_path)
	if controller != null and controller.has_method("SetDebugShowHitMask"):
		controller.call("SetDebugShowHitMask", _hit_mask_debug_visible)


func _toggle_glasses_visible() -> void:
	var current := _get_material_toggle_visible("get_glasses_visible", false)
	_set_material_toggle_visible("set_glasses_visible", not current)
	_update_context_menu_checks()


func _toggle_wings_visible() -> void:
	var current := _get_material_toggle_visible("get_wings_visible", false)
	_set_material_toggle_visible("set_wings_visible", not current)
	_update_context_menu_checks()


func _get_material_toggle_visible(method_name: String, fallback: bool) -> bool:
	var controller := get_node_or_null(material_toggle_controller_path)
	if controller != null and controller.has_method(method_name):
		return bool(controller.call(method_name))
	return fallback


func _set_material_toggle_visible(method_name: String, visible: bool) -> void:
	var controller := get_node_or_null(material_toggle_controller_path)
	if controller != null and controller.has_method(method_name):
		controller.call(method_name, visible)
	else:
		push_warning("Material toggle controller not found or missing %s: %s" % [method_name, str(material_toggle_controller_path)])


func _set_anime_render_preset(next_preset_name: String) -> void:
	var controller := get_node_or_null(anime_render_controller_path)
	if controller != null and controller.has_method("set_preset_name"):
		controller.call("set_preset_name", next_preset_name)
	else:
		push_warning("Anime render controller not found or missing set_preset_name: %s" % str(anime_render_controller_path))
	_update_context_menu_checks()


func _get_anime_render_preset() -> String:
	var controller := get_node_or_null(anime_render_controller_path)
	if controller != null and controller.has_method("get_preset_name"):
		return String(controller.call("get_preset_name"))
	return ""


func _set_window_interaction_lock(locked: bool) -> void:
	var controller := get_node_or_null(win_click_controller_path)
	if controller != null and controller.has_method("SetInteractionLock"):
		controller.call("SetInteractionLock", locked)


func _set_context_menu_interactive_rect(enabled: bool) -> void:
	var controller := get_node_or_null(win_click_controller_path)
	if controller == null or not controller.has_method("SetExtraInteractiveRect"):
		return
	var menu_rect := _get_context_menu_rect()
	controller.call("SetExtraInteractiveRect", menu_rect, enabled)


func _refresh_context_menu_interactive_rect() -> void:
	if _context_panel != null and _context_panel.visible:
		_set_context_menu_interactive_rect(true)


func _get_context_menu_rect() -> Rect2:
	if _context_panel == null:
		return Rect2()
	var panel_size := _context_panel.size
	if panel_size.x <= 1.0 or panel_size.y <= 1.0:
		panel_size = _context_panel.custom_minimum_size
	return Rect2(_context_panel.global_position, panel_size)


func _set_light_mode(mode: int) -> void:
	var lighting_controller := get_node_or_null(lighting_controller_path)
	if lighting_controller != null and lighting_controller.has_method("set_light_mode"):
		lighting_controller.call("set_light_mode", mode)
		print("Light menu selected mode: %s" % str(mode))
	else:
		push_warning("Lighting controller not found or missing set_light_mode: %s" % str(lighting_controller_path))
	_update_context_menu_checks()


func _get_light_mode() -> int:
	var lighting_controller := get_node_or_null(lighting_controller_path)
	if lighting_controller != null and lighting_controller.has_method("get_light_mode"):
		return int(lighting_controller.call("get_light_mode"))
	return LIGHT_FOLLOW_SYSTEM


func _handle_window_resize_input(event: InputEvent) -> bool:
	if not ctrl_wheel_resize:
		return false

	if event is InputEventMouseButton:
		var mouse_button := event as InputEventMouseButton
		if not mouse_button.pressed or not _has_ctrl_modifier(mouse_button):
			return false
		if mouse_button.button_index != MOUSE_BUTTON_WHEEL_UP and mouse_button.button_index != MOUSE_BUTTON_WHEEL_DOWN:
			return false

		var wheel_direction := 1 if mouse_button.button_index == MOUSE_BUTTON_WHEEL_UP else -1
		_resize_window_by_steps(wheel_direction)
		get_viewport().set_input_as_handled()
		return true

	if event is InputEventKey:
		var key_event := event as InputEventKey
		if not key_event.pressed or key_event.echo or not _has_ctrl_modifier(key_event):
			return false
		if key_event.keycode == KEY_EQUAL or key_event.keycode == KEY_PLUS or key_event.keycode == KEY_KP_ADD:
			_resize_window_by_steps(1)
			get_viewport().set_input_as_handled()
			return true
		if key_event.keycode == KEY_MINUS or key_event.keycode == KEY_KP_SUBTRACT:
			_resize_window_by_steps(-1)
			get_viewport().set_input_as_handled()
			return true

	return false


func _has_ctrl_modifier(event: InputEvent) -> bool:
	if event is InputEventWithModifiers:
		var modified_event := event as InputEventWithModifiers
		return modified_event.ctrl_pressed or Input.is_key_pressed(KEY_CTRL)
	return Input.is_key_pressed(KEY_CTRL)


func _resize_window_by_steps(direction: int) -> void:
	var current_size := DisplayServer.window_get_size()
	var aspect := float(window_size.x) / maxf(float(window_size.y), 1.0)
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

	window_size = new_size
	_apply_fixed_size_constraints(new_size)
	DisplayServer.window_set_size(new_size)
	DisplayServer.window_set_position(centered_position)
	call_deferred("_request_hit_mask_rebuild")


func _apply_fixed_size_constraints(size: Vector2i) -> void:
	var window := get_window()
	window.unresizable = true
	window.min_size = size
	window.max_size = size
	DisplayServer.window_set_min_size(size)
	DisplayServer.window_set_max_size(size)


func _request_hit_mask_rebuild() -> void:
	await get_tree().process_frame
	var polygon := get_node_or_null(hit_mask_polygon_path)
	if polygon != null and polygon.has_method("force_rebuild"):
		polygon.call("force_rebuild")


func _apply_desktop_input_modes() -> void:
	if not disable_free_camera_input:
		return
	var free_camera := get_node_or_null(free_camera_path)
	if free_camera != null:
		free_camera.set("enabled", false)
