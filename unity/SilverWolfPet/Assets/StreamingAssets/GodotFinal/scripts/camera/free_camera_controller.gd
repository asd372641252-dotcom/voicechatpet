extends Camera3D

@export var enabled := true
@export var target := Vector3(0.0, 0.88, 0.0)
@export var distance := 3.4
@export var min_distance := 1.4
@export var max_distance := 9.0
@export var yaw_degrees := 0.0
@export var pitch_degrees := 0.0
@export var min_pitch_degrees := -55.0
@export var max_pitch_degrees := 65.0
@export var zoom_step := 0.28
@export var orthographic_zoom_step := 0.18
@export var min_orthographic_size := 0.75
@export var max_orthographic_size := 6.0
@export var rotate_sensitivity := 0.22
@export var pan_sensitivity := 0.0018

var _rotating := false
var _panning := false
var _default_orthographic_size := 2.68


func _ready() -> void:
	_default_orthographic_size = size
	_apply_camera_transform()


func _process(_delta: float) -> void:
	if _rotating and not Input.is_mouse_button_pressed(MOUSE_BUTTON_RIGHT):
		_rotating = false
	if _panning and not Input.is_mouse_button_pressed(MOUSE_BUTTON_MIDDLE):
		_panning = false


func _unhandled_input(event: InputEvent) -> void:
	if not enabled:
		return

	if event is InputEventMouseButton:
		var mouse_button := event as InputEventMouseButton
		if mouse_button.button_index == MOUSE_BUTTON_WHEEL_UP and not mouse_button.ctrl_pressed:
			_zoom(-zoom_step)
			get_viewport().set_input_as_handled()
			return
		if mouse_button.button_index == MOUSE_BUTTON_WHEEL_DOWN and not mouse_button.ctrl_pressed:
			_zoom(zoom_step)
			get_viewport().set_input_as_handled()
			return
		if mouse_button.button_index == MOUSE_BUTTON_RIGHT:
			_rotating = mouse_button.pressed
			get_viewport().set_input_as_handled()
			return
		if mouse_button.button_index == MOUSE_BUTTON_MIDDLE:
			_panning = mouse_button.pressed
			get_viewport().set_input_as_handled()
			return

	if event is InputEventMouseMotion:
		var motion := event as InputEventMouseMotion
		if _rotating:
			yaw_degrees -= motion.relative.x * rotate_sensitivity
			pitch_degrees = clampf(
				pitch_degrees - motion.relative.y * rotate_sensitivity,
				min_pitch_degrees,
				max_pitch_degrees
			)
			_apply_camera_transform()
			get_viewport().set_input_as_handled()
			return
		if _panning:
			_pan(motion.relative)
			get_viewport().set_input_as_handled()
			return


func reset_view() -> void:
	target = Vector3(0.0, 0.88, 0.0)
	distance = 3.4
	size = _default_orthographic_size
	yaw_degrees = 0.0
	pitch_degrees = 0.0
	_apply_camera_transform()


func zoom_steps(steps: int) -> void:
	if projection == PROJECTION_ORTHOGONAL:
		_zoom_orthographic(-orthographic_zoom_step * float(steps))
		return
	_zoom(-zoom_step * float(steps))


func _zoom(delta: float) -> void:
	if projection == PROJECTION_ORTHOGONAL:
		_zoom_orthographic(delta)
		return
	distance = clampf(distance + delta, min_distance, max_distance)
	_apply_camera_transform()


func _zoom_orthographic(delta: float) -> void:
	size = clampf(size + delta, min_orthographic_size, max_orthographic_size)
	_apply_camera_transform()


func _pan(relative: Vector2) -> void:
	var pan_scale := pan_sensitivity * distance
	if projection == PROJECTION_ORTHOGONAL:
		pan_scale = pan_sensitivity * size
	target += (-global_transform.basis.x * relative.x + global_transform.basis.y * relative.y) * pan_scale
	_apply_camera_transform()


func _apply_camera_transform() -> void:
	var yaw := Basis(Vector3.UP, deg_to_rad(yaw_degrees))
	var pitch := Basis(Vector3.RIGHT, deg_to_rad(pitch_degrees))
	var orbit_basis := yaw * pitch
	var offset := orbit_basis * Vector3(0.0, 0.0, distance)
	global_position = target + offset
	look_at(target, Vector3.UP)
