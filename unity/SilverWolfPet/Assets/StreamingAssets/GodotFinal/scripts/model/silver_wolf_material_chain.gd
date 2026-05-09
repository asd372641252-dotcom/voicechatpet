extends RefCounted

# PMX-derived material handling for Silver Wolf LV.999.
# This keeps normal desktop rendering close to the source MMD material table,
# while leaving wing/glasses visibility to the runtime toggles.

const RUNTIME_TOGGLE_MATERIALS := {
	"小翼骨": true,
	"小光翼AL": true,
	"大翼骨": true,
	"大光翼AL": true,
	"鏡片": true,
	"鏡片+": true,
	"目镜": true,
	"目鏡+": true,
}

const DOUBLE_SIDED_MATERIALS := {
	"特效区衣摆+": true,
	"特效区衣摆++": true,
	"特效区衣摆": true,
}

const ALPHA_SCISSOR_MATERIALS := {
	"下擺+": true,
	"衣灯条": true,
	"特效区衣摆+": true,
	"特效区衣摆++": true,
	"特效区衣摆": true,
	"biaoq": true,
}

const PMX_ALPHA := {
	"顏+": 0.0,
	"目影": 0.3,
}


func apply_surface(mesh_instance: MeshInstance3D, surface_index: int, base_material: Material) -> bool:
	if not (base_material is StandardMaterial3D):
		if mesh_instance.get_surface_override_material(surface_index) != null:
			mesh_instance.set_surface_override_material(surface_index, null)
		return false

	var material_name := _canonical_material_name((base_material as StandardMaterial3D).resource_name)
	if RUNTIME_TOGGLE_MATERIALS.has(material_name):
		return false

	var override_material: Material = mesh_instance.get_surface_override_material(surface_index)
	if not (override_material is StandardMaterial3D):
		override_material = (base_material as StandardMaterial3D).duplicate()
		if override_material == null:
			return false
		mesh_instance.set_surface_override_material(surface_index, override_material)

	_apply_pmx_surface(override_material as StandardMaterial3D, material_name)
	return true


func _apply_pmx_surface(material: StandardMaterial3D, material_name: String) -> void:
	var alpha := float(PMX_ALPHA.get(material_name, 1.0))

	material.albedo_color = Color(1.0, 1.0, 1.0, alpha)
	material.emission_enabled = false
	material.emission_texture = null
	material.emission_energy_multiplier = 0.0
	material.metallic = 0.0
	material.roughness = 0.86
	material.metallic_specular = 0.0
	material.specular_mode = BaseMaterial3D.SPECULAR_DISABLED
	material.diffuse_mode = BaseMaterial3D.DIFFUSE_BURLEY
	material.disable_receive_shadows = true
	material.cull_mode = BaseMaterial3D.CULL_DISABLED if DOUBLE_SIDED_MATERIALS.has(material_name) else BaseMaterial3D.CULL_BACK

	if alpha <= 0.001:
		material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
		return

	if material_name == "目影":
		material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
		return

	if ALPHA_SCISSOR_MATERIALS.has(material_name):
		material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA_SCISSOR
		material.alpha_scissor_threshold = 0.28
		return

	material.transparency = BaseMaterial3D.TRANSPARENCY_DISABLED


func _canonical_material_name(name: String) -> String:
	var output := String(name)
	var dot_index := output.rfind(".")
	if dot_index > 0:
		var suffix := output.substr(dot_index + 1)
		if suffix.is_valid_int():
			output = output.substr(0, dot_index)
	return output
