extends Node

const SilverWolfMaterialChain = preload("res://scripts/model/silver_wolf_material_chain.gd")

@export var model_loader_path := NodePath("../ModelSlot")
@export var enabled := true
@export var preserve_runtime_presentation := false
@export var desktop_wings_expanded := false
@export var desktop_glasses_enabled := true
@export var anime_toon_preset_enabled := false
@export var desktop_main_surface_tint := Color(0.86, 0.86, 0.86, 1.0)
@export var desktop_effect_surface_tint := Color(0.92, 0.96, 1.0, 0.58)
@export_range(0.0, 1.0, 0.01) var toon_roughness := 0.8
@export_range(0.0, 1.0, 0.01) var toon_specular := 0.04
@export_range(0.0, 1.0, 0.01) var toon_alpha_scissor_threshold := 0.35

var _material_chain
var _materials_applied := false


func _ready() -> void:
	_material_chain = SilverWolfMaterialChain.new()
	_connect_model_loader()
	call_deferred("apply_to_current_model")


func set_wings_expanded(expanded: bool) -> void:
	desktop_wings_expanded = expanded
	apply_to_current_model()


func set_glasses_enabled(show_glasses: bool) -> void:
	desktop_glasses_enabled = show_glasses
	apply_to_current_model()


func apply_to_current_model() -> void:
	var model_root := _get_model_root()
	if model_root == null:
		return

	if not enabled or preserve_runtime_presentation:
		_clear_material_overrides(model_root)
		return

	var mesh_instances: Array[MeshInstance3D] = []
	_collect_meshes(model_root, mesh_instances)
	for mesh_instance in mesh_instances:
		if mesh_instance.mesh == null:
			continue
		mesh_instance.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
		for surface_index in range(mesh_instance.mesh.get_surface_count()):
			_apply_surface(mesh_instance, surface_index)

	_materials_applied = true


func _apply_surface(mesh_instance: MeshInstance3D, surface_index: int) -> void:
	var base_material: Material = mesh_instance.mesh.surface_get_material(surface_index)
	if base_material == null:
		base_material = mesh_instance.get_active_material(surface_index)

	if _material_chain != null and _material_chain.apply_surface(mesh_instance, surface_index, base_material):
		return

	if not (base_material is StandardMaterial3D):
		mesh_instance.set_surface_override_material(surface_index, null)
		return

	var material_role := _get_material_role(base_material as StandardMaterial3D)
	if not _should_override_material(material_role):
		mesh_instance.set_surface_override_material(surface_index, null)
		return

	var override_material: Material = mesh_instance.get_surface_override_material(surface_index)
	if not (override_material is StandardMaterial3D):
		override_material = (base_material as StandardMaterial3D).duplicate()
		if override_material == null:
			return
		mesh_instance.set_surface_override_material(surface_index, override_material)

	_tune_material(override_material as StandardMaterial3D)


func _should_override_material(role: String) -> bool:
	return role in [
		"glow_wing",
		"wing_core",
		"effect_strip",
		"visor_surface",
		"face_surface",
		"hair_surface",
		"skin_body",
		"body_overlay",
		"body",
	]


func _get_material_role(material: StandardMaterial3D) -> String:
	var material_name := String(material.resource_name)
	var material_name_lower := material_name.to_lower()
	var albedo_file := ""
	var emission_file := ""
	if material.albedo_texture != null:
		albedo_file = String(material.albedo_texture.resource_path).get_file()
	if material.emission_texture != null:
		emission_file = String(material.emission_texture.resource_path).get_file()

	var is_body_overlay := material.transparency != BaseMaterial3D.TRANSPARENCY_DISABLED
	var is_visor_surface := (
		material_name_lower.contains("glass")
		or material_name_lower.contains("visor")
		or material_name_lower.contains("glasses")
		or albedo_file.ends_with("_镜.png")
	)
	var is_glow_wing := albedo_file.ends_with("_光翼.png") or material_name_lower.contains("glow")
	var is_wing_core := (albedo_file.ends_with("_翼.png") and not is_glow_wing) or material_name_lower.contains("wing")
	var is_effect_strip := albedo_file.ends_with("_AL.png") or emission_file.ends_with("_AL.png") or material_name_lower.contains("effect")
	var is_face_surface := albedo_file.ends_with("_颜.png") or material_name_lower.contains("face")
	var is_hair_surface := albedo_file.ends_with("_髪.png") or material_name_lower.contains("hair")
	var is_body_surface := albedo_file.ends_with("_衣.png") or material_name_lower.contains("body") or material_name_lower.contains("cloth")
	var is_skin_body := material_name_lower.contains("skin")
	is_body_overlay = is_body_overlay and not is_glow_wing and not is_wing_core and not is_effect_strip and not is_visor_surface and not is_face_surface and not is_hair_surface

	if is_glow_wing:
		return "glow_wing"
	if is_wing_core:
		return "wing_core"
	if is_effect_strip:
		return "effect_strip"
	if is_visor_surface:
		return "visor_surface"
	if is_face_surface:
		return "face_surface"
	if is_hair_surface:
		return "hair_surface"
	if is_skin_body:
		return "skin_body"
	if is_body_overlay:
		return "body_overlay"
	if is_body_surface:
		return "body"
	return "other"


func _tune_material(material: StandardMaterial3D) -> void:
	var material_role := _get_material_role(material)
	match material_role:
		"glow_wing":
			_tune_glow_wing(material)
		"wing_core":
			_tune_wing_core(material)
		"effect_strip":
			_tune_effect_strip(material)
		"visor_surface":
			_tune_visor(material)
		"face_surface":
			_apply_texture_direct_main_surface(material, false, true)
		"hair_surface":
			_apply_texture_direct_main_surface(material, true, true)
		"skin_body":
			_apply_texture_direct_main_surface(material, false, true)
		"body_overlay":
			_tune_body_overlay(material)
		"body":
			_apply_texture_direct_main_surface(material, true, false)


func _tune_glow_wing(material: StandardMaterial3D) -> void:
	if desktop_wings_expanded:
		material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA_DEPTH_PRE_PASS
		material.albedo_color = Color(0.94, 0.98, 1.0, 0.68)
		material.emission_enabled = true
		material.emission = Color(0.56, 0.78, 1.0, 1.0)
		material.emission_energy_multiplier = 0.18
		material.metallic = 0.0
		material.metallic_specular = toon_specular
		material.roughness = toon_roughness
		material.cull_mode = BaseMaterial3D.CULL_DISABLED
		material.disable_receive_shadows = true
	else:
		material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
		material.albedo_color = Color(1.0, 1.0, 1.0, 0.0)
		material.emission_enabled = true
		material.emission = Color(0.88, 0.93, 1.0, 1.0)
		material.emission_energy_multiplier = 0.0
		material.roughness = 1.0
		material.disable_receive_shadows = true


func _tune_wing_core(material: StandardMaterial3D) -> void:
	if desktop_wings_expanded:
		material.transparency = BaseMaterial3D.TRANSPARENCY_DISABLED
		material.albedo_color = Color(1.0, 1.0, 1.0, 1.0)
		material.roughness = toon_roughness
		material.metallic = 0.0
		material.metallic_specular = toon_specular
		material.cull_mode = BaseMaterial3D.CULL_DISABLED
		material.disable_receive_shadows = true
	else:
		material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
		material.albedo_color = Color(1.0, 1.0, 1.0, 0.0)
		material.roughness = 0.92
		material.metallic = 0.04
		material.disable_receive_shadows = true


func _tune_effect_strip(material: StandardMaterial3D) -> void:
	material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA_DEPTH_PRE_PASS
	material.alpha_scissor_threshold = toon_alpha_scissor_threshold
	material.albedo_color = desktop_effect_surface_tint
	material.emission_enabled = true
	material.emission = Color(0.48, 0.72, 1.0, 1.0)
	material.emission_energy_multiplier = 0.22
	material.metallic = 0.0
	material.metallic_specular = toon_specular
	material.roughness = toon_roughness
	material.cull_mode = BaseMaterial3D.CULL_DISABLED
	material.disable_receive_shadows = true


func _tune_visor(material: StandardMaterial3D) -> void:
	if desktop_glasses_enabled:
		material.transparency = BaseMaterial3D.TRANSPARENCY_DISABLED
		material.albedo_color = Color(1.0, 1.0, 1.0, 1.0)
		material.emission_enabled = false
		material.metallic = 0.0
		material.metallic_specular = 0.03
		material.roughness = clampf(maxf(material.roughness, 0.72), 0.72, 1.0)
		material.disable_receive_shadows = false
	else:
		material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
		material.albedo_color = Color(1.0, 1.0, 1.0, 0.0)
		material.emission_enabled = false
		material.disable_receive_shadows = true


func _tune_body_overlay(material: StandardMaterial3D) -> void:
	material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA_SCISSOR
	material.alpha_scissor_threshold = toon_alpha_scissor_threshold
	material.albedo_color = desktop_main_surface_tint
	material.emission_enabled = false
	material.metallic = 0.0
	material.metallic_specular = toon_specular
	material.roughness = toon_roughness
	if anime_toon_preset_enabled:
		material.diffuse_mode = BaseMaterial3D.DIFFUSE_TOON
	material.specular_mode = BaseMaterial3D.SPECULAR_DISABLED
	material.cull_mode = BaseMaterial3D.CULL_DISABLED
	material.disable_receive_shadows = false


func _apply_texture_direct_main_surface(material: StandardMaterial3D, disable_cull: bool, disable_specular: bool) -> void:
	material.transparency = BaseMaterial3D.TRANSPARENCY_DISABLED
	material.albedo_color = desktop_main_surface_tint
	material.emission_enabled = false
	material.metallic = 0.0
	material.roughness = toon_roughness
	if anime_toon_preset_enabled:
		material.diffuse_mode = BaseMaterial3D.DIFFUSE_TOON
	if disable_cull:
		material.cull_mode = BaseMaterial3D.CULL_DISABLED
	material.disable_receive_shadows = false
	if disable_specular:
		material.specular_mode = BaseMaterial3D.SPECULAR_DISABLED
		material.metallic_specular = 0.0
	else:
		material.metallic_specular = toon_specular


func _clear_material_overrides(model_root: Node) -> void:
	if not _materials_applied:
		return
	var mesh_instances: Array[MeshInstance3D] = []
	_collect_meshes(model_root, mesh_instances)
	for mesh_instance in mesh_instances:
		if mesh_instance.mesh == null:
			continue
		for surface_index in range(mesh_instance.mesh.get_surface_count()):
			if mesh_instance.get_surface_override_material(surface_index) != null:
				mesh_instance.set_surface_override_material(surface_index, null)
	_materials_applied = false


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


func _collect_meshes(node: Node, meshes: Array[MeshInstance3D]) -> void:
	if node is MeshInstance3D:
		meshes.append(node)
	for child in node.get_children():
		_collect_meshes(child, meshes)
