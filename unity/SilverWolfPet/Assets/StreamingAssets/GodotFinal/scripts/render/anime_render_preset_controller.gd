extends Node

signal preset_applied(preset_name: String)

@export var model_loader_path := NodePath("../ModelSlot")
@export var model_container_path := NodePath("../ModelSlot")
@export_file("*.glb", "*.vrm") var model_path := "res://assets/converted/user_pet_model.glb"
@export var auto_load_model := false
@export var model_position := Vector3(0.0, -0.95, 0.0)
@export var model_scale := Vector3.ONE
@export var model_rotation_degrees := Vector3.ZERO
@export var auto_fit_to_view := true
@export var target_height := 2.45
@export var target_center_y := 1.08
@export var target_center_z := -0.35
@export var camera_path := NodePath("../Camera3D")
@export var world_environment_path := NodePath("../WorldEnvironment")
@export var key_light_path := NodePath("../KeyLight")
@export var rim_light_path := NodePath("../RimLight")
@export_file("*.json") var slot_map_path := "res://config/silverwolf_material_slot_map.json"
@export_file("*.json") var preset_path := "res://config/anime_render_presets.json"
@export var preset_name := "DesktopPetSoft"
@export var auto_apply_on_ready := true
@export var outline_enabled := true
@export var force_transparent_background := true
@export var wings_visible := false
@export var glasses_visible := false

const TOON_SHADER_PATH := "res://shaders/anime_toon.shader"
const FACE_SHADER_PATH := "res://shaders/anime_face.shader"
const EYE_SHADER_PATH := "res://shaders/anime_eye_unlit.shader"
const OUTLINE_SHADER_PATH := "res://shaders/anime_outline.shader"

const EYE_SLOTS := {
	"42": {"2": true, "6": true, "7": true, "8": true, "38": true},
	"38": {"2": true, "6": true, "7": true, "8": true, "36": true},
}

const FACE_SLOTS := {
	"42": {"0": true, "1": true, "3": true, "4": true, "5": true},
	"38": {"0": true, "1": true, "3": true, "4": true, "5": true},
}

var _texture_paths: Dictionary = {}
var _textures: Dictionary = {}
var _slot_maps: Dictionary = {}
var _presets: Dictionary = {}
var _outline_nodes: Array[MeshInstance3D] = []
var _shaders: Dictionary = {}
var _direct_model: Node
var _hidden_material: StandardMaterial3D


func _ready() -> void:
	_load_slot_map()
	_load_textures()
	_load_presets()
	_connect_model_loader()
	if auto_load_model:
		_load_direct_model()
	if auto_apply_on_ready:
		call_deferred("apply_current_preset")


func set_preset_name(next_preset_name: String) -> void:
	preset_name = next_preset_name
	apply_current_preset()


func get_preset_name() -> String:
	return preset_name


func get_available_presets() -> Array:
	return _presets.keys()


func set_wings_visible(visible: bool) -> void:
	wings_visible = visible
	apply_current_preset()


func get_wings_visible() -> bool:
	return wings_visible


func set_glasses_visible(visible: bool) -> void:
	glasses_visible = visible
	apply_current_preset()


func get_glasses_visible() -> bool:
	return glasses_visible


func apply_current_preset(tonemap_override := "") -> void:
	var preset := _get_preset(preset_name)
	if preset.is_empty():
		push_warning("Anime render preset missing: %s" % preset_name)
		return

	_apply_camera(preset)
	_apply_environment(preset, tonemap_override)
	_apply_light(preset)

	var model_root := _get_model_root()
	if model_root == null:
		return

	_clear_outlines()
	_apply_materials(model_root, preset)
	if outline_enabled and _can_draw_whole_mesh_outline() and bool(preset.get("outline", {}).get("enabled", true)):
		_add_outlines(model_root, preset)
	var loader_node := get_node_or_null(model_loader_path)
	if loader_node is Node3D:
		(loader_node as Node3D).visible = true
	preset_applied.emit(preset_name)


func _connect_model_loader() -> void:
	var loader := get_node_or_null(model_loader_path)
	if loader == null:
		return
	if loader.has_signal("model_loaded") and not loader.model_loaded.is_connected(_on_model_loaded):
		loader.model_loaded.connect(_on_model_loaded)


func _on_model_loaded(_model_root: Node) -> void:
	call_deferred("apply_current_preset")


func _get_model_root() -> Node:
	if _direct_model != null and is_instance_valid(_direct_model):
		return _direct_model
	var loader := get_node_or_null(model_loader_path)
	if loader == null:
		return null
	if loader.has_method("get_model_root"):
		var model_root = loader.call("get_model_root")
		if model_root is Node:
			return model_root
	return null


func _load_direct_model() -> void:
	var container := get_node_or_null(model_container_path)
	if container == null:
		return
	var packed: PackedScene = load(model_path)
	if packed == null:
		push_warning("Anime render test model missing: %s" % model_path)
		return
	_direct_model = packed.instantiate()
	var preset := _get_preset(preset_name)
	if not preset.is_empty():
		_apply_materials(_direct_model, preset)
	container.add_child(_direct_model)
	_apply_model_transform(_direct_model)
	if auto_fit_to_view:
		_fit_model_to_view(_direct_model)


func _apply_model_transform(model_root: Node) -> void:
	if model_root is Node3D:
		var node_3d := model_root as Node3D
		node_3d.position = model_position
		node_3d.rotation_degrees = model_rotation_degrees
		node_3d.scale = model_scale


func _load_slot_map() -> void:
	_texture_paths.clear()
	_slot_maps.clear()
	var parsed := _load_json(slot_map_path)
	if parsed.is_empty():
		push_warning("Anime render slot map missing: %s" % slot_map_path)
		return

	var texture_dir := String(parsed.get("texture_dir", "res://assets/converted")).trim_suffix("/")
	var texture_files: Dictionary = parsed.get("texture_files", {})
	for role_variant in texture_files.keys():
		var role := String(role_variant)
		var file_name := String(texture_files[role_variant])
		_texture_paths[role] = file_name if file_name.begins_with("res://") else "%s/%s" % [texture_dir, file_name]

	var maps: Dictionary = parsed.get("slot_maps", {})
	for count_variant in maps.keys():
		var source_map = maps[count_variant]
		if not (source_map is Dictionary):
			continue
		var normalized := {}
		for surface_variant in source_map.keys():
			normalized[String(surface_variant)] = String(source_map[surface_variant])
		_slot_maps[String(count_variant)] = normalized


func _load_textures() -> void:
	_textures.clear()
	for role_variant in _texture_paths.keys():
		var role := String(role_variant)
		var texture := load(String(_texture_paths[role]))
		if texture is Texture2D:
			_textures[role] = texture
		else:
			push_warning("Anime render texture missing for role %s: %s" % [role, String(_texture_paths[role])])


func _load_presets() -> void:
	var parsed := _load_json(preset_path)
	_presets = parsed.get("presets", {})
	if preset_name.is_empty():
		preset_name = String(parsed.get("default_preset", "DesktopPetSoft"))


func _get_preset(name: String) -> Dictionary:
	if _presets.has(name):
		return (_presets[name] as Dictionary).duplicate(true)
	return {}


func _apply_camera(preset: Dictionary) -> void:
	var camera := get_node_or_null(camera_path) as Camera3D
	if camera == null:
		return
	var camera_config: Dictionary = preset.get("camera", {})
	camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	camera.size = float(camera_config.get("orthogonal_size", 2.8))
	camera.position = _vector3_from_array(camera_config.get("position", []), Vector3(0.0, 1.03, 4.4))
	camera.rotation = Vector3.ZERO
	camera.near = 0.01
	camera.far = 100.0
	camera.current = true


func _apply_environment(preset: Dictionary, tonemap_override: String) -> void:
	var world_environment := get_node_or_null(world_environment_path) as WorldEnvironment
	if world_environment == null:
		return

	var world_config: Dictionary = preset.get("world", {})
	var env := Environment.new()
	env.background_mode = Environment.BG_CLEAR_COLOR if force_transparent_background else Environment.BG_COLOR
	env.background_color = _color_from_array(world_config.get("background_color", []), Color(0.10, 0.11, 0.13, 1.0))
	if force_transparent_background:
		env.background_color.a = 0.0
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = _color_from_array(world_config.get("ambient_color", []), Color(0.34, 0.36, 0.42, 1.0))
	env.ambient_light_energy = float(world_config.get("ambient_energy", 0.38))
	env.tonemap_exposure = float(world_config.get("exposure", 1.0))
	var tonemap := tonemap_override if not tonemap_override.is_empty() else String(world_config.get("tonemap", "linear"))
	env.tonemap_mode = Environment.TONE_MAPPER_FILMIC if tonemap == "filmic" else Environment.TONE_MAPPER_LINEAR
	env.adjustment_enabled = false
	var glow: Dictionary = world_config.get("glow", {})
	_set_property_if_available(env, "glow_enabled", bool(glow.get("enabled", false)))
	_set_property_if_available(env, "glow_normalized", bool(glow.get("normalized", false)))
	_set_property_if_available(env, "glow_intensity", float(glow.get("intensity", 0.38)))
	_set_property_if_available(env, "glow_strength", float(glow.get("strength", 0.82)))
	_set_property_if_available(env, "glow_bloom", float(glow.get("bloom", 0.16)))
	_set_property_if_available(env, "glow_hdr_threshold", float(glow.get("hdr_threshold", 0.72)))
	_set_property_if_available(env, "glow_hdr_scale", float(glow.get("hdr_scale", 1.25)))
	_set_property_if_available(env, "glow_hdr_luminance_cap", float(glow.get("hdr_luminance_cap", 8.0)))
	_set_property_if_available(env, "ssao_enabled", false)
	_set_property_if_available(env, "ssr_enabled", false)
	_set_property_if_available(env, "auto_exposure_enabled", false)
	world_environment.environment = env
	var camera := get_node_or_null(camera_path) as Camera3D
	if camera != null:
		camera.environment = env
	if force_transparent_background:
		get_tree().root.transparent_bg = true
		get_viewport().transparent_bg = true
		if RenderingServer.has_method("viewport_set_transparent_background"):
			RenderingServer.call("viewport_set_transparent_background", get_viewport().get_viewport_rid(), true)
		DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_TRANSPARENT, true)
		RenderingServer.set_default_clear_color(Color(0.0, 0.0, 0.0, 0.0))


func _apply_light(preset: Dictionary) -> void:
	var key_light := get_node_or_null(key_light_path) as DirectionalLight3D
	var light_config: Dictionary = preset.get("light", {})
	if key_light != null:
		_configure_directional_light(
			key_light,
			light_config,
			Vector3(-34.0, -28.0, 0.0),
			Color(1.0, 0.94, 0.90, 1.0),
			0.55
		)

	var rim_config: Dictionary = preset.get("rim_light", {})
	var rim_light := get_node_or_null(rim_light_path) as DirectionalLight3D
	if rim_light == null and bool(rim_config.get("enabled", false)):
		rim_light = DirectionalLight3D.new()
		rim_light.name = "RimLight"
		get_parent().add_child(rim_light)
		rim_light_path = NodePath("../RimLight")
	if rim_light != null:
		rim_light.visible = bool(rim_config.get("enabled", false))
		_configure_directional_light(
			rim_light,
			rim_config,
			Vector3(-26.0, 142.0, 0.0),
			Color(0.48, 0.62, 1.0, 1.0),
			0.18
		)


func _configure_directional_light(light: DirectionalLight3D, config: Dictionary, fallback_rotation: Vector3, fallback_color: Color, fallback_energy: float) -> void:
	light.light_energy = float(config.get("energy", fallback_energy))
	light.light_color = _color_from_array(config.get("color", []), fallback_color)
	light.rotation_degrees = _vector3_from_array(config.get("rotation_degrees", []), fallback_rotation)
	light.shadow_enabled = bool(config.get("shadow_enabled", false))


func _apply_materials(model_root: Node, preset: Dictionary) -> void:
	for mesh_node in model_root.find_children("*", "MeshInstance3D", true, false):
		var mesh_instance := mesh_node as MeshInstance3D
		if mesh_instance == null or mesh_instance.mesh == null:
			continue
		mesh_instance.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
		var count_key := str(mesh_instance.mesh.get_surface_count())
		var role_map: Dictionary = _slot_maps.get(count_key, {})
		if role_map.is_empty():
			mesh_instance.visible = true
			continue
		mesh_instance.visible = true
		for surface_index in range(mesh_instance.mesh.get_surface_count()):
			var role := String(role_map.get(str(surface_index), ""))
			if role.is_empty():
				continue
			if not _role_is_visible(role):
				mesh_instance.set_surface_override_material(surface_index, _make_hidden_material())
				continue
			var category := _category_for_surface(count_key, surface_index, role)
			var material := _make_material_for_category(category, role, preset)
			if material != null:
				mesh_instance.set_surface_override_material(surface_index, material)


func _category_for_surface(count_key: String, surface_index: int, role: String) -> String:
	var index_key := str(surface_index)
	if EYE_SLOTS.get(count_key, {}).has(index_key):
		return "eye"
	if role in ["skin", "skin_body"]:
		return "skin"
	if FACE_SLOTS.get(count_key, {}).has(index_key):
		return "face"
	if role == "hair":
		return "hair"
	if role in ["wing", "glow_wing", "glass", "head_glasses_frame", "head_glasses_lens", "eye_glasses", "vrm_glass", "vrm_eye_glasses"]:
		return "accessory"
	if role in ["effect", "expression"]:
		return "effect"
	return "clothes"


func _make_material_for_category(category: String, role: String, preset: Dictionary) -> Material:
	var texture: Texture2D = _textures.get(role)
	if texture == null:
		return null

	match category:
		"face", "skin":
			return _make_face_material(texture, role, preset)
		"eye", "effect":
			return _make_eye_material(texture, role, preset)
		_:
			return _make_toon_material(texture, category, role, preset)


func _role_is_visible(role: String) -> bool:
	if role in ["wing", "glow_wing"]:
		return wings_visible
	if role in ["eye_glasses", "vrm_eye_glasses"]:
		return glasses_visible
	return true


func _can_draw_whole_mesh_outline() -> bool:
	return wings_visible and glasses_visible


func _make_hidden_material() -> StandardMaterial3D:
	if _hidden_material != null:
		return _hidden_material
	_hidden_material = StandardMaterial3D.new()
	_hidden_material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	_hidden_material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	_hidden_material.albedo_color = Color(1.0, 1.0, 1.0, 0.0)
	_hidden_material.disable_receive_shadows = true
	return _hidden_material


func _make_toon_material(texture: Texture2D, category: String, role: String, preset: Dictionary) -> ShaderMaterial:
	var material := ShaderMaterial.new()
	material.shader = _get_shader("toon", TOON_SHADER_PATH)
	var toon: Dictionary = preset.get("toon", {})
	var rim: Dictionary = preset.get("rim", {})
	material.set_shader_parameter("albedo_texture", texture)
	material.set_shader_parameter("albedo_tint", _tint_for_category(category, role, preset))
	material.set_shader_parameter("shadow_color", _shadow_color_for_category(category, preset))
	material.set_shader_parameter("deep_shadow_color", _deep_shadow_color_for_category(category, preset))
	material.set_shader_parameter("cool_shadow_color", _color_from_array(toon.get("cool_shadow_color", []), Color(0.42, 0.46, 0.68, 1.0)))
	material.set_shader_parameter("warm_skin_shadow_color", _color_from_array(toon.get("warm_skin_shadow_color", []), Color(0.98, 0.70, 0.78, 1.0)))
	material.set_shader_parameter("rim_color", _color_from_array(rim.get("color", []), Color(0.54, 0.72, 1.0, 1.0)))
	material.set_shader_parameter("shadow_threshold", float(toon.get("shadow_threshold", 0.46)))
	material.set_shader_parameter("shadow_softness", float(toon.get("shadow_softness", 0.08)))
	material.set_shader_parameter("second_shadow_threshold", float(toon.get("second_shadow_threshold", 0.18)))
	material.set_shader_parameter("shadow_strength", float(toon.get("shadow_strength", 0.72)))
	material.set_shader_parameter("cool_shadow_strength", _cool_shadow_strength_for_category(category, preset))
	material.set_shader_parameter("warm_skin_shadow_strength", _warm_shadow_strength_for_category(category, preset))
	material.set_shader_parameter("rim_strength", _rim_strength_for_category(category, role, preset))
	material.set_shader_parameter("rim_power", float(rim.get("power", 3.0)))
	var specular := float(toon.get("specular_strength", 0.02))
	if category not in ["accessory", "clothes"]:
		specular = minf(specular, 0.008)
	material.set_shader_parameter("specular_strength", specular)
	material.set_shader_parameter("emission_strength", _emission_strength_for_role(category, role, preset))
	material.set_shader_parameter("emission_tint", _emission_tint_for_role(role, preset))
	material.set_shader_parameter("brightness", float(toon.get("brightness", 1.0)))
	material.set_shader_parameter("force_opaque", role in ["eye_glasses", "vrm_eye_glasses", "head_glasses_frame"])
	return material


func _make_face_material(texture: Texture2D, role: String, preset: Dictionary) -> ShaderMaterial:
	var material := ShaderMaterial.new()
	material.shader = _get_shader("face", FACE_SHADER_PATH)
	var face: Dictionary = preset.get("face", {})
	var rim: Dictionary = preset.get("rim", {})
	material.set_shader_parameter("base_texture", texture)
	material.set_shader_parameter("base_tint", _skin_tint_for_role(role, preset))
	material.set_shader_parameter("face_shadow_color", _color_from_array(face.get("shadow_color", []), Color(0.94, 0.74, 0.78, 1.0)))
	material.set_shader_parameter("face_shadow_strength", float(face.get("shadow_strength", 0.14)))
	material.set_shader_parameter("face_shadow_threshold", float(face.get("shadow_threshold", 0.18)))
	material.set_shader_parameter("face_shadow_softness", float(face.get("shadow_softness", 0.30)))
	material.set_shader_parameter("face_rim_color", _color_from_array(rim.get("color", []), Color(0.54, 0.72, 1.0, 1.0)))
	material.set_shader_parameter("face_rim_strength", float(face.get("rim_strength", 0.0)))
	material.set_shader_parameter("face_rim_power", float(rim.get("power", 3.5)))
	material.set_shader_parameter("face_brightness", float(face.get("brightness", 1.10)))
	return material


func _make_eye_material(texture: Texture2D, role: String, preset: Dictionary) -> ShaderMaterial:
	var material := ShaderMaterial.new()
	material.shader = _get_shader("eye", EYE_SHADER_PATH)
	var eye: Dictionary = preset.get("eye", {})
	material.set_shader_parameter("base_texture", texture)
	material.set_shader_parameter("base_tint", _emission_tint_for_role(role, preset) if role in ["effect", "expression"] else Color.WHITE)
	material.set_shader_parameter("brightness", float(eye.get("brightness", 1.08)))
	material.set_shader_parameter("emission_strength", _emission_strength_for_role("eye", role, preset, float(eye.get("emission_strength", 0.20))))
	return material


func _add_outlines(model_root: Node, preset: Dictionary) -> void:
	var outline_config: Dictionary = preset.get("outline", {})
	var material := ShaderMaterial.new()
	material.shader = _get_shader("outline", OUTLINE_SHADER_PATH)
	material.set_shader_parameter("outline_width", float(outline_config.get("width", 0.006)))
	material.set_shader_parameter("outline_color", _color_from_array(outline_config.get("color", []), Color(0.12, 0.10, 0.18, 1.0)))

	for mesh_node in model_root.find_children("*", "MeshInstance3D", true, false):
		var mesh_instance := mesh_node as MeshInstance3D
		if mesh_instance == null or mesh_instance.mesh == null:
			continue
		var outline := MeshInstance3D.new()
		outline.name = "%s_AnimeOutline" % mesh_instance.name
		outline.mesh = mesh_instance.mesh
		outline.transform = mesh_instance.transform
		outline.layers = mesh_instance.layers
		outline.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
		outline.material_override = material
		outline.set("skeleton", mesh_instance.get("skeleton"))
		outline.set("skin", mesh_instance.get("skin"))
		mesh_instance.get_parent().add_child(outline)
		_outline_nodes.append(outline)


func _clear_outlines() -> void:
	for outline in _outline_nodes:
		if is_instance_valid(outline):
			outline.queue_free()
	_outline_nodes.clear()


func _tint_for_category(category: String, role: String, _preset: Dictionary) -> Color:
	var preset := _preset
	var tints: Dictionary = preset.get("category_tints", {})
	if role in ["body"] and tints.has("white_cloth"):
		return _color_from_array(tints.get("white_cloth", []), Color(0.92, 0.92, 0.95, 1.0))
	if category == "clothes" and role in ["cloth", "clothes", "white", "body"]:
		return _color_from_array(tints.get("clothes", []), Color(0.90, 0.90, 0.93, 1.0))
	if category == "skin" and tints.has("skin"):
		return _skin_tint_for_role(role, preset)
	if category == "hair" and tints.has("hair"):
		return _color_from_array(tints.get("hair", []), Color(0.88, 0.90, 1.0, 1.0))
	match category:
		"hair":
			return Color(0.88, 0.90, 1.0, 1.0)
		"skin":
			return _skin_tint_for_role(role, preset)
		"clothes":
			return Color(0.90, 0.90, 0.93, 1.0)
		"accessory":
			if role in ["eye_glasses", "vrm_eye_glasses"]:
				return Color(0.98, 0.96, 1.0, 1.0)
			if role == "head_glasses_lens":
				return Color(0.92, 0.92, 1.0, 1.0)
			return Color(0.86, 0.88, 0.94, 1.0)
		_:
			return Color.WHITE


func _skin_tint_for_role(role: String, preset: Dictionary = {}) -> Color:
	var tints: Dictionary = preset.get("category_tints", {})
	if role == "skin_body":
		return _color_from_array(tints.get("skin_body", []), Color(1.0, 0.92, 0.90, 1.0))
	return _color_from_array(tints.get("skin", []), Color(1.0, 0.92, 0.90, 1.0))


func _shadow_color_for_category(category: String, preset: Dictionary) -> Color:
	var colors: Dictionary = preset.get("category_colors", {})
	var key := "%s_shadow" % category
	if colors.has(key):
		return _color_from_array(colors.get(key, []), Color(0.70, 0.66, 0.78, 1.0))
	match category:
		"hair":
			return Color(0.58, 0.62, 0.82, 1.0)
		"skin":
			return Color(0.93, 0.70, 0.78, 1.0)
		"clothes":
			return Color(0.58, 0.57, 0.68, 1.0)
		"accessory":
			return Color(0.50, 0.52, 0.64, 1.0)
		_:
			return Color(0.70, 0.66, 0.78, 1.0)


func _deep_shadow_color_for_category(category: String, preset: Dictionary) -> Color:
	var colors: Dictionary = preset.get("category_colors", {})
	var key := "%s_deep_shadow" % category
	if colors.has(key):
		return _color_from_array(colors.get(key, []), Color(0.42, 0.40, 0.52, 1.0))
	match category:
		"hair":
			return Color(0.38, 0.42, 0.62, 1.0)
		"skin":
			return Color(0.76, 0.50, 0.62, 1.0)
		"clothes":
			return Color(0.34, 0.34, 0.46, 1.0)
		"accessory":
			return Color(0.32, 0.34, 0.46, 1.0)
		_:
			return Color(0.42, 0.40, 0.52, 1.0)


func _rim_strength_for_category(category: String, role: String, preset: Dictionary) -> float:
	var rim: Dictionary = preset.get("rim", {})
	var strengths: Dictionary = rim.get("strength_by_category", {})
	var value := float(strengths.get(category, rim.get("strength", 0.0)))
	if role in ["effect", "glow_wing", "wing", "head_glasses_lens", "eye_glasses", "vrm_eye_glasses"]:
		value = maxf(value, float(rim.get("emissive_strength", value)))
	return value


func _cool_shadow_strength_for_category(category: String, preset: Dictionary) -> float:
	var toon: Dictionary = preset.get("toon", {})
	var values: Dictionary = toon.get("cool_shadow_strength_by_category", {})
	return float(values.get(category, toon.get("cool_shadow_strength", 0.0)))


func _warm_shadow_strength_for_category(category: String, preset: Dictionary) -> float:
	var toon: Dictionary = preset.get("toon", {})
	var values: Dictionary = toon.get("warm_skin_shadow_strength_by_category", {})
	return float(values.get(category, 0.0))


func _emission_strength_for_role(category: String, role: String, preset: Dictionary, fallback := 0.0) -> float:
	var emission: Dictionary = preset.get("emission", {})
	var values: Dictionary = emission.get("strength_by_role", {})
	if values.has(role):
		return float(values.get(role))
	if category == "effect":
		return float(emission.get("effect", fallback))
	if role in ["glow_wing"]:
		return float(emission.get("glow_wing", fallback))
	if role in ["head_glasses_lens", "eye_glasses", "vrm_eye_glasses"]:
		return float(emission.get("glass", fallback))
	return fallback


func _emission_tint_for_role(role: String, preset: Dictionary) -> Color:
	var emission: Dictionary = preset.get("emission", {})
	var colors: Dictionary = emission.get("tint_by_role", {})
	if colors.has(role):
		return _color_from_array(colors.get(role, []), Color(0.45, 0.72, 1.0, 1.0))
	return _color_from_array(emission.get("tint", []), Color(0.45, 0.72, 1.0, 1.0))


func _load_json(path: String) -> Dictionary:
	if path.is_empty() or not FileAccess.file_exists(path):
		return {}
	var parsed = JSON.parse_string(FileAccess.get_file_as_string(path))
	return parsed if parsed is Dictionary else {}


func _get_shader(shader_key: String, path: String) -> Shader:
	if _shaders.has(shader_key):
		return _shaders[shader_key]
	var shader := Shader.new()
	shader.code = FileAccess.get_file_as_string(path)
	_shaders[shader_key] = shader
	return shader


func _vector3_from_array(value, fallback: Vector3) -> Vector3:
	if typeof(value) != TYPE_ARRAY or value.size() < 3:
		return fallback
	return Vector3(float(value[0]), float(value[1]), float(value[2]))


func _color_from_array(value, fallback: Color) -> Color:
	if typeof(value) != TYPE_ARRAY or value.size() < 4:
		return fallback
	return Color(float(value[0]), float(value[1]), float(value[2]), float(value[3]))


func _set_property_if_available(object: Object, property_name: String, value) -> void:
	for property in object.get_property_list():
		if String(property.get("name", "")) == property_name:
			object.set(property_name, value)
			return


func _fit_model_to_view(model_root: Node) -> void:
	if not (model_root is Node3D):
		return
	var meshes: Array[MeshInstance3D] = []
	_collect_meshes(model_root, meshes)
	var bounds := _merged_mesh_bounds(meshes)
	if bounds.size == Vector3.ZERO:
		return

	var node_3d := model_root as Node3D
	var height := maxf(bounds.size.y, 0.001)
	node_3d.scale *= target_height / height
	node_3d.force_update_transform()

	meshes.clear()
	_collect_meshes(model_root, meshes)
	bounds = _merged_mesh_bounds(meshes)
	if bounds.size == Vector3.ZERO:
		return

	var center := bounds.get_center()
	node_3d.global_position += Vector3(
		-center.x,
		target_center_y - center.y,
		target_center_z - center.z
	)


func _collect_meshes(node: Node, meshes: Array[MeshInstance3D]) -> void:
	if node is MeshInstance3D:
		var mesh_instance := node as MeshInstance3D
		if mesh_instance.visible and mesh_instance.mesh != null:
			meshes.append(mesh_instance)
	for child in node.get_children():
		_collect_meshes(child, meshes)


func _merged_mesh_bounds(meshes: Array[MeshInstance3D]) -> AABB:
	var merged := AABB()
	var has_bounds := false
	for mesh_instance in meshes:
		var world := mesh_instance.global_transform * mesh_instance.get_aabb()
		if not has_bounds:
			merged = world
			has_bounds = true
		else:
			merged = merged.merge(world)
	return merged if has_bounds else AABB()
