extends WorldEnvironment

@export var camera_path := NodePath("../Camera3D")


func _enter_tree() -> void:
	_apply_transparent_environment()


func _ready() -> void:
	_apply_transparent_environment()


func _apply_transparent_environment() -> void:
	var transparent_environment := Environment.new()
	transparent_environment.background_mode = Environment.BG_CLEAR_COLOR
	transparent_environment.background_color = Color(0.0, 0.0, 0.0, 0.0)
	transparent_environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	transparent_environment.ambient_light_color = Color.WHITE
	transparent_environment.ambient_light_energy = 0.10
	environment = transparent_environment

	var camera := get_node_or_null(camera_path)
	if camera is Camera3D:
		(camera as Camera3D).environment = transparent_environment
