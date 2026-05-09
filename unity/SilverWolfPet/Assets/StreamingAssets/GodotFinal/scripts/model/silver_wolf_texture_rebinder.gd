extends Node

@export var model_loader_path := NodePath("../ModelSlot")
@export_file("*.json") var slot_map_path := "res://config/silverwolf_material_slot_map.json"
@export var target_surface_count := 42
@export var enabled := true
@export var main_surface_tint := Color(0.86, 0.86, 0.86, 1.0)
@export var face_surface_tint := Color(0.94, 0.92, 0.92, 1.0)
@export var wings_visible := false
@export var glasses_visible := false

const FALLBACK_TEXTURE_FILES := {
	"face": "silver_wolf_lv999_face.png",
	"skin": "silver_wolf_lv999_face.png",
	"skin_body": "silver_wolf_lv999_body.png",
	"body": "silver_wolf_lv999_body.png",
	"hair": "silver_wolf_lv999_hair.png",
	"wing": "silver_wolf_lv999_wing.png",
	"glow_wing": "silver_wolf_lv999_glow_wing.png",
	"glass": "silver_wolf_lv999_glass.png",
	"head_glasses_frame": "silver_wolf_lv999_glass.png",
	"head_glasses_lens": "silver_wolf_lv999_glass.png",
	"eye_glasses": "silver_wolf_lv999_glass.png",
	"vrm_glass": "Avatar_SilverWolf999_00_Glasses_Color.png",
	"vrm_eye_glasses": "Avatar_SilverWolf999_00_Glasses_Color.png",
	"effect": "silver_wolf_lv999_effect_al.png",
	"expression": "silver_wolf_lv999_expression.png",
	"shadow": "silver_wolf_lv999_shadow.jpg",
}

const FALLBACK_SURFACE_ROLES := {
	"0": "skin",
	"1": "face",
	"2": "face",
	"3": "face",
	"4": "face",
	"5": "face",
	"6": "face",
	"7": "face",
	"8": "face",
	"9": "skin_body",
	"10": "skin_body",
	"11": "skin_body",
	"12": "hair",
	"13": "body",
	"14": "body",
	"15": "body",
	"16": "effect",
	"17": "body",
	"18": "body",
	"19": "body",
	"20": "body",
	"21": "body",
	"22": "body",
	"23": "effect",
	"24": "body",
	"25": "body",
	"26": "body",
	"27": "head_glasses_frame",
	"28": "effect",
	"29": "effect",
	"30": "effect",
	"31": "effect",
	"32": "shadow",
	"33": "wing",
	"34": "glow_wing",
	"35": "wing",
	"36": "glow_wing",
	"37": "head_glasses_lens",
	"38": "expression",
	"39": "head_glasses_lens",
	"40": "eye_glasses",
	"41": "eye_glasses",
}

var _texture_paths: Dictionary = {}
var _textures: Dictionary = {}
var _surface_roles: Dictionary = {}
var _surface_role_maps: Dictionary = {}
var _surface_count := 42


func _ready() -> void:
	_load_slot_map()
	_load_textures()
	_connect_model_loader()
	call_deferred("apply_to_current_model")


func reload_bindings() -> void:
	_load_slot_map()
	_load_textures()
	apply_to_current_model()


func set_wings_visible(visible: bool) -> void:
	wings_visible = visible
	apply_to_current_model()


func get_wings_visible() -> bool:
	return wings_visible


func set_glasses_visible(visible: bool) -> void:
	glasses_visible = visible
	apply_to_current_model()


func get_glasses_visible() -> bool:
	return glasses_visible


func apply_to_current_model() -> void:
	if not enabled:
		return
	var model_root := _get_model_root()
	if model_root == null:
		return
	_apply_recursive(model_root)


func _connect_model_loader() -> void:
	var loader := get_node_or_null(model_loader_path)
	if loader == null:
		return
	if loader.has_signal("model_loaded") and not loader.model_loaded.is_connected(_on_model_loaded):
		loader.model_loaded.connect(_on_model_loaded)


func _on_model_loaded(_model_root: Node) -> void:
	apply_to_current_model()


func _get_model_root() -> Node:
	var loader := get_node_or_null(model_loader_path)
	if loader == null:
		return null
	if loader.has_method("get_model_root"):
		var model_root = loader.call("get_model_root")
		if model_root is Node:
			return model_root
	return null


func _load_slot_map() -> void:
	_surface_count = target_surface_count
	_surface_roles = FALLBACK_SURFACE_ROLES.duplicate()
	_surface_role_maps = {"42": FALLBACK_SURFACE_ROLES.duplicate()}
	_texture_paths = _build_texture_paths("res://assets/converted", FALLBACK_TEXTURE_FILES)

	if not FileAccess.file_exists(slot_map_path):
		push_warning("SilverWolf slot map missing, using fallback: %s" % slot_map_path)
		return

	var parsed = JSON.parse_string(FileAccess.get_file_as_string(slot_map_path))
	if not (parsed is Dictionary):
		push_warning("SilverWolf slot map is not a JSON object: %s" % slot_map_path)
		return

	_surface_count = int(parsed.get("mesh_surface_count", _surface_count))

	var texture_dir := String(parsed.get("texture_dir", "res://assets/converted")).trim_suffix("/")
	var texture_files = parsed.get("texture_files", {})
	if texture_files is Dictionary:
		_texture_paths = _build_texture_paths(texture_dir, texture_files)

	var surface_roles = parsed.get("surface_roles", {})
	if surface_roles is Dictionary:
		_surface_roles.clear()
		for surface_index_variant in surface_roles.keys():
			_surface_roles[String(surface_index_variant)] = String(surface_roles[surface_index_variant])
		_surface_role_maps[str(_surface_count)] = _surface_roles.duplicate()

	var slot_maps = parsed.get("slot_maps", {})
	if slot_maps is Dictionary:
		_surface_role_maps.clear()
		for surface_count_variant in slot_maps.keys():
			var source_map = slot_maps[surface_count_variant]
			if not (source_map is Dictionary):
				continue
			var normalized_map := {}
			for surface_index_variant in source_map.keys():
				normalized_map[String(surface_index_variant)] = String(source_map[surface_index_variant])
			_surface_role_maps[String(surface_count_variant)] = normalized_map


func _build_texture_paths(texture_dir: String, texture_files: Dictionary) -> Dictionary:
	var output := {}
	for role_variant in texture_files.keys():
		var role := String(role_variant)
		var file_name := String(texture_files[role_variant])
		if file_name.begins_with("res://") or file_name.begins_with("user://"):
			output[role] = file_name
		else:
			output[role] = "%s/%s" % [texture_dir, file_name]
	return output


func _load_textures() -> void:
	_textures.clear()
	for role_variant in _texture_paths.keys():
		var role := String(role_variant)
		var path := String(_texture_paths[role])
		var texture := load(path)
		if texture is Texture2D:
			_textures[role] = texture
		else:
			push_warning("SilverWolf texture missing for role %s: %s" % [role, path])


func _apply_recursive(node: Node) -> void:
	if node is MeshInstance3D:
		_apply_mesh(node as MeshInstance3D)
	for child in node.get_children():
		_apply_recursive(child)


func _apply_mesh(mesh_instance: MeshInstance3D) -> void:
	var mesh := mesh_instance.mesh
	if mesh == null:
		return
	var roles := _roles_for_surface_count(mesh.get_surface_count())
	if roles.is_empty():
		return

	for surface_index in range(mesh.get_surface_count()):
		var role := String(roles.get(str(surface_index), ""))
		if role.is_empty():
			continue
		_apply_material_role(mesh_instance, surface_index, role)


func _roles_for_surface_count(surface_count: int) -> Dictionary:
	var count_key := str(surface_count)
	if _surface_role_maps.has(count_key):
		return _surface_role_maps[count_key]
	if surface_count == _surface_count:
		return _surface_roles
	return {}


func _apply_material_role(mesh_instance: MeshInstance3D, surface_index: int, role: String) -> void:
	var texture: Texture2D = _textures.get(role)
	if texture == null:
		return

	var base_material := mesh_instance.get_active_material(surface_index)
	if base_material == null and mesh_instance.mesh != null:
		base_material = mesh_instance.mesh.surface_get_material(surface_index)

	var material := mesh_instance.get_surface_override_material(surface_index)
	if not (material is StandardMaterial3D):
		if base_material is StandardMaterial3D:
			material = (base_material as StandardMaterial3D).duplicate()
		else:
			material = StandardMaterial3D.new()
		mesh_instance.set_surface_override_material(surface_index, material)

	var standard := material as StandardMaterial3D
	standard.resource_name = "silver_wolf_slot_%02d_%s" % [surface_index, role]
	standard.albedo_texture = texture
	standard.albedo_color = _albedo_tint_for_role(role)
	standard.metallic = 0.0
	standard.roughness = maxf(standard.roughness, 0.72)
	standard.disable_receive_shadows = false
	if role in ["face", "skin", "skin_body"]:
		standard.metallic_specular = 0.18

	if not _role_is_visible(role):
		_hide_toggle_surface(standard)
		return

	match role:
		"effect", "glow_wing":
			standard.emission_enabled = true
			standard.emission_texture = texture
			standard.emission = Color.WHITE
			standard.emission_energy_multiplier = 0.18 if role == "glow_wing" else 0.12
			standard.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA_DEPTH_PRE_PASS
			standard.cull_mode = BaseMaterial3D.CULL_DISABLED
		"expression":
			standard.emission_enabled = false
			standard.emission_texture = null
			standard.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA_SCISSOR
			standard.alpha_scissor_threshold = 0.28
			standard.cull_mode = BaseMaterial3D.CULL_DISABLED
		"shadow":
			standard.emission_enabled = false
			standard.emission_texture = null
			standard.albedo_color = Color(1.0, 1.0, 1.0, 0.3)
			standard.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
		"hair", "wing", "glass", "vrm_glass", "head_glasses_lens":
			standard.emission_enabled = false
			standard.emission_texture = null
			standard.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA_DEPTH_PRE_PASS
			standard.cull_mode = BaseMaterial3D.CULL_DISABLED
		"head_glasses_frame", "eye_glasses", "vrm_eye_glasses":
			standard.emission_enabled = false
			standard.emission_texture = null
			standard.transparency = BaseMaterial3D.TRANSPARENCY_DISABLED
			standard.cull_mode = BaseMaterial3D.CULL_DISABLED
		_:
			standard.emission_enabled = false
			standard.emission_texture = null
			standard.transparency = BaseMaterial3D.TRANSPARENCY_DISABLED


func _albedo_tint_for_role(role: String) -> Color:
	match role:
		"face":
			return face_surface_tint
		"skin", "skin_body":
			return Color(1.0, 0.86, 0.82, 1.0)
		"body":
			return main_surface_tint
		"hair", "wing", "glass", "vrm_glass", "head_glasses_lens":
			return main_surface_tint
		"head_glasses_frame", "eye_glasses", "vrm_eye_glasses":
			return Color(1.0, 1.0, 1.0, 1.0)
		_:
			return Color.WHITE


func _role_is_visible(role: String) -> bool:
	if role in ["wing", "glow_wing"]:
		return wings_visible
	if role in ["eye_glasses", "vrm_eye_glasses"]:
		return glasses_visible
	return true


func _hide_toggle_surface(material: StandardMaterial3D) -> void:
	material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	material.albedo_color = Color(1.0, 1.0, 1.0, 0.0)
	material.emission_enabled = false
	material.emission_texture = null
	material.emission_energy_multiplier = 0.0
	material.disable_receive_shadows = true
	material.cull_mode = BaseMaterial3D.CULL_DISABLED
