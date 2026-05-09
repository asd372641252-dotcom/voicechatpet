extends SceneTree


const SCENE_PATH := "res://scenes/test_kawaii_action.tscn"
const TARGET_SHAPES := ["大口", "口下", "口", "ワ", "あ"]


func _initialize() -> void:
	call_deferred("_run")


func _run() -> void:
	var packed := load(SCENE_PATH)
	if packed == null:
		print("SCENE_LOAD_FAIL ", SCENE_PATH)
		quit(1)
		return

	var scene: Node = packed.instantiate()
	get_root().add_child(scene)
	await process_frame
	await process_frame
	await process_frame

	var controller := scene.get_node_or_null("ExpressionController")
	print("EXPR_CONTROLLER ", controller != null)
	if controller != null and controller.has_method("set_expression_weight"):
		var ok: bool = bool(controller.call("set_expression_weight", "mouth_open", 1.0))
		print("SET_MOUTH_OPEN ", ok)
	else:
		print("SET_MOUTH_OPEN false")

	await process_frame
	_print_shape_values(scene, "after_controller")
	_set_target_shapes_direct(scene, 1.0)
	_print_shape_values(scene, "after_direct")
	quit()


func _print_shape_values(root: Node, label: String) -> void:
	print("SHAPE_VALUES ", label)
	_walk_print(root)


func _walk_print(node: Node) -> void:
	if node is MeshInstance3D:
		var mesh_instance := node as MeshInstance3D
		var mesh := mesh_instance.mesh
		if mesh != null:
			var count: int = mesh.get_blend_shape_count()
			for index in range(count):
				var shape_name := str(mesh.get_blend_shape_name(index))
				if TARGET_SHAPES.has(shape_name):
					print("  ", shape_name, "=", mesh_instance.get_blend_shape_value(index))

	for child in node.get_children():
		_walk_print(child)


func _set_target_shapes_direct(root: Node, weight: float) -> void:
	_walk_set(root, weight)


func _walk_set(node: Node, weight: float) -> void:
	if node is MeshInstance3D:
		var mesh_instance := node as MeshInstance3D
		var mesh := mesh_instance.mesh
		if mesh != null:
			var count: int = mesh.get_blend_shape_count()
			for index in range(count):
				var shape_name := str(mesh.get_blend_shape_name(index))
				if TARGET_SHAPES.has(shape_name):
					mesh_instance.set_blend_shape_value(index, weight)

	for child in node.get_children():
		_walk_set(child, weight)
