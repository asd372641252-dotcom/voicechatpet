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
	await _wait_frames(4)

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
		"bubble_text": "字幕先到，但音频还没开始。",
	})
	await _wait_frames(10)
	print("GATE subtitle_only")
	_print_values(scene)

	server.call("_handle_command", {
		"type": "pet_pose",
		"state": "speaking",
		"emotion": "neutral",
		"gesture": "none",
		"posture": "stand",
		"mouth": "audio_volume",
		"mouth_open": 0.7,
		"audio_active": true,
		"overlay_only": true,
	})
	await _wait_frames(10)
	print("GATE audio_active")
	_print_values(scene)

	server.call("_handle_command", {
		"type": "pet_pose",
		"state": "speaking",
		"emotion": "neutral",
		"gesture": "none",
		"posture": "stand",
		"mouth": "audio_volume",
		"mouth_open": 0.0,
		"audio_active": false,
		"overlay_only": true,
	})
	await _wait_frames(4)
	print("GATE audio_stopped")
	_print_values(scene)

	quit()


func _wait_frames(count: int) -> void:
	for _index in range(count):
		await process_frame


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
