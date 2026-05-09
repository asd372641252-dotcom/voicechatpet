extends SceneTree


const MODEL_PATH := "res://assets/converted/user_pet_model.glb"


func _initialize() -> void:
	var packed := load(MODEL_PATH)
	if packed == null:
		print("LOAD_FAIL ", MODEL_PATH)
		quit(1)
		return

	var root: Node = packed.instantiate()
	get_root().add_child(root)
	print("MODEL ", MODEL_PATH)
	_walk(root)
	quit()


func _walk(node: Node) -> void:
	if node is MeshInstance3D:
		var mesh_instance := node as MeshInstance3D
		var mesh := mesh_instance.mesh
		if mesh != null:
			var count: int = mesh.get_blend_shape_count()
			if count > 0:
				print("MESH ", str(mesh_instance.get_path()), " mesh=", mesh.resource_name, " blend_shapes=", count)
				for index in range(count):
					print("  ", index, ": ", mesh.get_blend_shape_name(index))

	for child in node.get_children():
		_walk(child)
