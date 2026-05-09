extends SceneTree

const SCENE_PATH := "res://scenes/test_kawaii_action.tscn"


func _initialize() -> void:
	call_deferred("_run")


func _run() -> void:
	var packed := load(SCENE_PATH) as PackedScene
	if packed == null:
		push_error("Unable to load scene: %s" % SCENE_PATH)
		quit(1)
		return
	var scene := packed.instantiate()
	root.add_child(scene)
	await process_frame
	var launcher := scene.get_node_or_null("VolcVoiceRuntimeLauncher")
	if launcher == null:
		push_error("VolcVoiceRuntimeLauncher not found.")
		quit(1)
		return
	launcher._force_stop_orphan_bridge_processes("res://scripts/run_volc_rtc_web_client.py", 17862)
	launcher._force_stop_known_bridge_processes()
	await create_timer(1.0).timeout
	quit(0)
