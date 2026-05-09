extends SceneTree


const SCENE_PATH := "res://scenes/test_kawaii_action.tscn"
const WATCH_SHAPES := ["あ", "大口", "口下", "口", "ワ", "お", "う", "ん", "にやり"]


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

	var server := scene.get_node_or_null("PetPoseServer")
	if server == null:
		print("SERVER false")
		quit(1)
		return

	server.call("_handle_command", {
		"type": "pet_pose",
		"state": "speaking",
		"emotion": "neutral",
		"gesture": "none",
		"posture": "stand",
		"mouth": "audio_volume",
		"bubble_text": "这都能点错？算了，我来。搞定。",
	})

	for step in range(20):
		await process_frame
		await process_frame
		await process_frame
		print("FLAP_STEP ", step)
		_print_values(scene)

	quit()


func _print_values(root: Node) -> void:
	_walk(root)


func _walk(node: Node) -> void:
	if node is MeshInstance3D:
		var mesh_instance := node as MeshInstance3D
		var mesh := mesh_instance.mesh
		if mesh != null:
			var count: int = mesh.get_blend_shape_count()
			var parts: Array[String] = []
			for index in range(count):
				var shape_name := str(mesh.get_blend_shape_name(index))
				if WATCH_SHAPES.has(shape_name):
					parts.append("%s=%.2f" % [shape_name, mesh_instance.get_blend_shape_value(index)])
			if not parts.is_empty():
				print("  ", " ".join(parts))

	for child in node.get_children():
		_walk(child)
