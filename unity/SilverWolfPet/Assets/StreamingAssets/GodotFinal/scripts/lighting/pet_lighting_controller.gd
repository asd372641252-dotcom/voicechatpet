extends Node

enum LightMode {
	FOLLOW_SYSTEM,
	SOFT,
	BRIGHT,
	DIM,
}

@export var key_light_path := NodePath("../KeyLight")
@export var fill_light_path := NodePath("../FillLight")
@export var environment_path := NodePath("../WorldEnvironment")
@export var camera_path := NodePath("../Camera3D")
@export var model_loader_path := NodePath("../ModelSlot")
@export var light_mode := LightMode.FOLLOW_SYSTEM


func _ready() -> void:
	_connect_model_loader()
	apply_light_mode(light_mode)


func set_light_mode(mode: int) -> void:
	light_mode = clampi(mode, LightMode.FOLLOW_SYSTEM, LightMode.DIM)
	apply_light_mode(light_mode)


func get_light_mode() -> int:
	return light_mode


func apply_light_mode(mode: int) -> void:
	var resolved_mode := mode
	if mode == LightMode.FOLLOW_SYSTEM:
		resolved_mode = LightMode.SOFT if _system_prefers_light() else LightMode.DIM

	match resolved_mode:
		LightMode.BRIGHT:
			_apply_profile(1.8, 0.46, 0.26, 1.16, Color(1.0, 0.96, 0.86))
		LightMode.DIM:
			_apply_profile(0.72, 0.14, 0.08, 0.88, Color(0.86, 0.91, 1.0))
		_:
			_apply_profile(0.92, 0.21, 0.14, 0.96, Color(1.0, 1.0, 1.0))


func _apply_profile(
	key_energy: float,
	fill_energy: float,
	ambient_energy: float,
	exposure: float,
	light_color: Color
) -> void:
	var key_light := get_node_or_null(key_light_path)
	if key_light is Light3D:
		var key := key_light as Light3D
		key.light_energy = key_energy
		key.light_color = light_color

	var fill_light := get_node_or_null(fill_light_path)
	if fill_light is Light3D:
		var fill := fill_light as Light3D
		fill.light_energy = fill_energy
		fill.light_color = light_color

	var world_environment := get_node_or_null(environment_path)
	if world_environment is WorldEnvironment:
		var env := (world_environment as WorldEnvironment).environment
		if env != null:
			env.background_mode = Environment.BG_CLEAR_COLOR
			env.background_color = Color(0.0, 0.0, 0.0, 0.0)
			env.ambient_light_energy = ambient_energy
			env.tonemap_exposure = exposure
			env.adjustment_enabled = false
			env.adjustment_brightness = 1.0
			env.adjustment_contrast = 1.0
			env.adjustment_saturation = 1.0

	var camera := get_node_or_null(camera_path)
	if camera is Camera3D and world_environment is WorldEnvironment:
		(camera as Camera3D).environment = (world_environment as WorldEnvironment).environment
		if (camera as Camera3D).environment != null:
			(camera as Camera3D).environment.background_mode = Environment.BG_CLEAR_COLOR
			(camera as Camera3D).environment.background_color = Color(0.0, 0.0, 0.0, 0.0)

	get_tree().root.transparent_bg = true
	get_viewport().transparent_bg = true
	if RenderingServer.has_method("viewport_set_transparent_background"):
		RenderingServer.call("viewport_set_transparent_background", get_viewport().get_viewport_rid(), true)
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_TRANSPARENT, true)
	RenderingServer.set_default_clear_color(Color(0.0, 0.0, 0.0, 0.0))

	_clear_model_overlays()
	print(
		"Lighting applied: mode=%s key=%.2f fill=%.2f ambient=%.3f exposure=%.2f"
		% [str(light_mode), key_energy, fill_energy, ambient_energy, exposure]
	)


func _connect_model_loader() -> void:
	var model_loader := get_node_or_null(model_loader_path)
	if model_loader == null:
		return
	if model_loader.has_signal("model_loaded") and not model_loader.model_loaded.is_connected(_on_model_loaded):
		model_loader.model_loaded.connect(_on_model_loaded)


func _on_model_loaded(_model_root: Node) -> void:
	_clear_model_overlays()


func _clear_model_overlays() -> void:
	var model_root := _get_model_root()
	if model_root == null:
		return

	var meshes: Array[MeshInstance3D] = []
	_collect_meshes(model_root, meshes)
	for mesh_instance in meshes:
		mesh_instance.material_overlay = null


func _get_model_root() -> Node:
	var model_loader := get_node_or_null(model_loader_path)
	if model_loader == null:
		return null
	if model_loader.has_method("get_model_root"):
		var model_root = model_loader.call("get_model_root")
		if model_root is Node:
			return model_root
	return null


func _collect_meshes(node: Node, meshes: Array[MeshInstance3D]) -> void:
	if node is MeshInstance3D:
		meshes.append(node)
	for child in node.get_children():
		_collect_meshes(child, meshes)


func _system_prefers_light() -> bool:
	if OS.get_name() != "Windows":
		return true

	var output: Array = []
	var exit_code := OS.execute(
		"powershell.exe",
		[
			"-NoProfile",
			"-WindowStyle",
			"Hidden",
			"-Command",
			"(Get-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize' -Name AppsUseLightTheme -ErrorAction SilentlyContinue).AppsUseLightTheme"
		],
		output,
		true,
		false
	)
	if exit_code != 0 or output.is_empty():
		return true

	return str(output[0]).strip_edges() != "0"
