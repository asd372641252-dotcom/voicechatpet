extends Node

@export var window_size := Vector2i(512, 720)
@export var lock_size := true


func _ready() -> void:
	var root_window := get_tree().root
	root_window.transparent_bg = true

	DisplayServer.window_set_mode(DisplayServer.WINDOW_MODE_WINDOWED)
	DisplayServer.window_set_size(window_size)
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_BORDERLESS, true)
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_ALWAYS_ON_TOP, true)

	if lock_size:
		DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_RESIZE_DISABLED, true)
		DisplayServer.window_set_min_size(window_size)
		DisplayServer.window_set_max_size(window_size)
