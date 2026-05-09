extends Node

@export var pet_player_path: NodePath = NodePath("..")
@export var status_label_path: NodePath = NodePath("../HUD/Panel/StatusLabel")
@export_file("*.json") var config_path := "res://config/volc_start_voice_chat.local.json"
@export_file("*.json") var voice_routes_config_path := "res://config/voice_routes.json"
@export var bridge_script_path := "res://scripts/run_volc_rtc_web_client.py"
@export var runtime_exe_path := "res://tools/volc_voice_runtime/bin/Release/net8.0-windows/VolcVoiceRuntime.exe"
@export var bridge_port := 17862
@export var godot_pose_port := 17865
@export var companion_polling_interval_sec := 10
@export var auto_start_url := true
@export var shutdown_grace_sec := 0.25
@export var cleanup_orphan_runtime_processes := true

var _pet_player: Node
var _status_label: Label
var _bridge_pid := -1
var _runtime_pid := -1
var _stopping_runtime := false
var _shutdown_in_progress := false
var _voice_routes: Dictionary = {}
var _active_route_id := "s2s_low_latency"
var _bridge_config_path := ""


func _ready() -> void:
	get_tree().auto_accept_quit = false
	_load_voice_routes_config()
	_pet_player = get_node_or_null(pet_player_path)
	_status_label = get_node_or_null(status_label_path) as Label
	if _pet_player != null:
		if _pet_player.has_signal("voice_chat_requested"):
			_pet_player.voice_chat_requested.connect(start_voice_runtime)
		if _pet_player.has_signal("voice_chat_stop_requested"):
			_pet_player.voice_chat_stop_requested.connect(stop_voice_runtime)
		if _pet_player.has_signal("app_quit_requested"):
			_pet_player.app_quit_requested.connect(shutdown_and_quit)
		if _pet_player.has_signal("screen_vision_start_requested"):
			_pet_player.screen_vision_start_requested.connect(start_screen_vision_runtime)
		if _pet_player.has_signal("screen_vision_stop_requested"):
			_pet_player.screen_vision_stop_requested.connect(stop_screen_vision_runtime)
		if _pet_player.has_signal("companion_polling_interval_requested"):
			_pet_player.companion_polling_interval_requested.connect(set_companion_polling_interval)


func _notification(what: int) -> void:
	if what == NOTIFICATION_WM_CLOSE_REQUEST:
		shutdown_and_quit()
	elif what == NOTIFICATION_PREDELETE:
		_force_stop_owned_processes()


func shutdown_and_quit() -> void:
	if _shutdown_in_progress:
		return
	_shutdown_in_progress = true
	await stop_voice_runtime()
	_force_stop_owned_processes()
	get_tree().auto_accept_quit = true
	get_tree().quit()


func start_voice_runtime(route_id := "") -> void:
	_select_voice_route(route_id)
	_set_status("Voice runtime starting (%s)..." % _route_display_name(_active_route_id))
	_start_bridge_if_needed()
	if not _route_requires_runtime_window(_active_route_id):
		_set_status("Agent plugin server started at http://127.0.0.1:%d" % _active_bridge_port())
		return
	await get_tree().create_timer(1.2).timeout
	_start_embedded_runtime(false)


func stop_voice_runtime() -> void:
	if _stopping_runtime:
		return
	_stopping_runtime = true
	_set_status("Voice chat stopping...")
	if _bridge_pid > 0 and OS.is_process_running(_bridge_pid):
		if _route_supports_vision(_active_route_id):
			await _post_bridge_json_async("/api/vision/stop", "Screen vision stop requested.")
		await _post_bridge_json_async("/api/stop_voice_chat", "Cloud voice session stopped.")
	else:
		await _post_bridge_json_async("/api/stop_voice_chat", "Cloud voice session stopped.")
	if is_inside_tree() and shutdown_grace_sec > 0.0:
		await get_tree().create_timer(shutdown_grace_sec).timeout
	_stop_pid(_runtime_pid)
	_runtime_pid = -1
	_stop_pid(_bridge_pid)
	_bridge_pid = -1
	_force_stop_owned_processes()
	_stopping_runtime = false
	_set_status("Voice chat stopped.")


func start_screen_vision_runtime(route_id := "") -> void:
	_select_voice_route(_direct_screen_vision_route_id(route_id))
	if not _route_supports_vision(_active_route_id):
		_set_status("当前语音路线不支持视觉；请选择火山方舟视觉模式。")
		return
	_set_status("Screen vision starting...")
	_start_bridge_if_needed()
	await get_tree().create_timer(0.8).timeout
	if _runtime_pid <= 0 or not OS.is_process_running(_runtime_pid):
		_start_embedded_runtime(true)
	await get_tree().create_timer(0.4).timeout
	if _active_route_id == "traditional_companion_polling":
		_post_bridge_json_body(
			"/api/companion_vision/interval",
			{"interval_sec": companion_polling_interval_sec},
			"Companion polling interval set to %d sec." % companion_polling_interval_sec
		)
	_post_bridge_json("/api/vision/start", "Screen vision requested.")


func stop_screen_vision_runtime() -> void:
	_set_status("Screen vision stopping...")
	_start_bridge_if_needed()
	await get_tree().create_timer(0.1).timeout
	_post_bridge_json("/api/vision/stop", "Screen vision stop requested.")


func set_companion_polling_interval(seconds: int) -> void:
	companion_polling_interval_sec = _normalize_companion_polling_interval(seconds)
	if _bridge_pid <= 0 or not OS.is_process_running(_bridge_pid):
		_set_status("Companion polling interval prepared: %d sec." % companion_polling_interval_sec)
		return
	if _active_route_id != "traditional_companion_polling":
		_set_status("Companion polling interval prepared: %d sec." % companion_polling_interval_sec)
		return
	_post_bridge_json_body(
		"/api/companion_vision/interval",
		{"interval_sec": companion_polling_interval_sec},
		"Companion polling interval set to %d sec." % companion_polling_interval_sec
	)


func _normalize_companion_polling_interval(seconds: int) -> int:
	if seconds <= 7:
		return 5
	if seconds <= 12:
		return 10
	return 15


func _start_bridge_if_needed() -> void:
	var selected_config := _globalize(config_path)
	var selected_script := _globalize(_active_bridge_script_path())
	var selected_port := _active_bridge_port()
	var selected_godot_pose_port := _active_godot_pose_port()
	var bridge_key := "%s|%s|%d|%d" % [selected_config, selected_script, selected_port, selected_godot_pose_port]
	if _bridge_pid > 0 and OS.is_process_running(_bridge_pid):
		if _bridge_config_path == bridge_key:
			return
		_stop_pid(_bridge_pid)
		_bridge_pid = -1
		_bridge_config_path = ""
	if _runtime_pid > 0 and OS.is_process_running(_runtime_pid):
		_stop_pid(_runtime_pid)
		_runtime_pid = -1
	if _bridge_pid > 0 and OS.is_process_running(_bridge_pid):
		return
	_force_stop_orphan_bridge_processes(selected_script, selected_port)
	var python_exe := _find_python_exe()
	if python_exe.is_empty():
		_set_status("python/pythonw not found; cannot start voice bridge.")
		push_warning("Volc voice runtime could not find pythonw/python in PATH.")
		return
	var args := [
		selected_script,
		"--config",
		selected_config,
		"--port",
		str(selected_port),
		"--godot-port",
		str(selected_godot_pose_port),
	]
	_bridge_pid = OS.create_process(python_exe, PackedStringArray(args), false)
	if _bridge_pid <= 0:
		_set_status("Voice bridge failed to start.")
		push_warning("Volc voice bridge failed to start.")
		return
	_bridge_config_path = bridge_key


func _start_embedded_runtime(request_vision := false) -> void:
	if _runtime_pid > 0 and OS.is_process_running(_runtime_pid):
		_set_status("Voice chat is already running.")
		return
	var exe := _globalize(runtime_exe_path)
	if not FileAccess.file_exists(exe):
		_set_status("Voice runtime is not built; run dotnet build first.")
		push_warning("Volc voice runtime exe missing: %s" % exe)
		return
	var query := "?autostart=1" if auto_start_url else ""
	if request_vision:
		query = "%s%svision=1" % [query, "&" if not query.is_empty() else "?"]
	var url := "http://127.0.0.1:%d/%s" % [_active_bridge_port(), query]
	_runtime_pid = OS.create_process(exe, PackedStringArray(["--url", url]), false)
	if _runtime_pid <= 0:
		_set_status("Voice runtime failed to start.")
		push_warning("Volc voice runtime failed to start: %s" % exe)
		return
	_set_status("Voice runtime started (%s); allow microphone if prompted." % _route_display_name(_active_route_id))


func _load_voice_routes_config() -> void:
	_voice_routes.clear()
	var path := voice_routes_config_path
	if path.is_empty() or not FileAccess.file_exists(path):
		_active_route_id = "s2s_low_latency"
		return
	var file := FileAccess.open(path, FileAccess.READ)
	if file == null:
		push_warning("Unable to read voice routes config: %s" % path)
		return
	var parsed = JSON.parse_string(file.get_as_text())
	if not (parsed is Dictionary):
		push_warning("Voice routes config is not an object: %s" % path)
		return
	var routes = parsed.get("routes", {})
	if routes is Dictionary:
		_voice_routes = routes
	var default_route := str(parsed.get("default_route", "s2s_low_latency"))
	_select_voice_route(default_route)


func _select_voice_route(route_id: String) -> void:
	var next_route := route_id if not route_id.is_empty() else _active_route_id
	if not _voice_routes.has(next_route):
		next_route = "s2s_low_latency"
	_active_route_id = next_route
	var info = _voice_routes.get(_active_route_id, {})
	if info is Dictionary and info.has("config_path"):
		config_path = str(info.get("config_path"))


func _route_display_name(route_id: String) -> String:
	var info = _voice_routes.get(route_id, {})
	if info is Dictionary:
		return str(info.get("display_name", route_id))
	return route_id


func _route_supports_vision(route_id: String) -> bool:
	var info = _voice_routes.get(route_id, {})
	return bool(info.get("supports_vision", false)) if info is Dictionary else false


func _direct_screen_vision_route_id(preferred_route_id := "") -> String:
	if not preferred_route_id.is_empty() and _voice_routes.has(preferred_route_id):
		var preferred = _voice_routes.get(preferred_route_id, {})
		if preferred is Dictionary and bool(preferred.get("supports_vision", false)) and not preferred_route_id.begins_with("s2s"):
			return preferred_route_id
	if _voice_routes.has("traditional_vision"):
		return "traditional_vision"
	for route_id in _voice_routes.keys():
		var route = _voice_routes.get(route_id, {})
		if route is Dictionary and bool(route.get("supports_vision", false)) and not str(route_id).begins_with("s2s"):
			return str(route_id)
	return preferred_route_id


func _route_requires_runtime_window(route_id: String) -> bool:
	var info = _voice_routes.get(route_id, {})
	if info is Dictionary and info.has("requires_runtime_window"):
		return bool(info.get("requires_runtime_window"))
	return true


func _active_bridge_script_path() -> String:
	var info = _voice_routes.get(_active_route_id, {})
	if info is Dictionary and info.has("bridge_script_path"):
		return str(info.get("bridge_script_path"))
	return bridge_script_path


func _active_bridge_port() -> int:
	var info = _voice_routes.get(_active_route_id, {})
	if info is Dictionary and info.has("bridge_port"):
		return int(info.get("bridge_port"))
	return bridge_port


func _active_godot_pose_port() -> int:
	var info = _voice_routes.get(_active_route_id, {})
	if info is Dictionary and info.has("godot_pose_port"):
		return int(info.get("godot_pose_port"))
	return godot_pose_port


func _find_python_exe() -> String:
	for candidate in ["pythonw", "pythonw.exe", "python", "python.exe"]:
		var output: Array = []
		var code := OS.execute(candidate, PackedStringArray(["--version"]), output, true, true)
		if code == 0:
			return candidate
	return ""


func _stop_pid(pid: int) -> void:
	if pid <= 0 or not OS.is_process_running(pid):
		return
	OS.kill(pid)


func _force_stop_owned_processes() -> void:
	_force_stop_orphan_runtime_processes()
	_force_stop_known_bridge_processes()


func _force_stop_orphan_runtime_processes() -> void:
	if not cleanup_orphan_runtime_processes:
		return
	if OS.get_name() != "Windows":
		return
	OS.execute(
		"powershell.exe",
		PackedStringArray([
			"-NoProfile",
			"-ExecutionPolicy",
			"Bypass",
			"-Command",
			"Get-Process -Name VolcVoiceRuntime -ErrorAction SilentlyContinue | Stop-Process -Force"
		]),
		[],
		false,
		false
	)


func _force_stop_known_bridge_processes() -> void:
	if not cleanup_orphan_runtime_processes:
		return
	if OS.get_name() != "Windows":
		return
	var candidates := {}
	_add_bridge_cleanup_candidate(candidates, bridge_script_path, bridge_port)
	for route_id in _voice_routes.keys():
		var info = _voice_routes.get(route_id, {})
		if not (info is Dictionary):
			continue
		var script := str(info.get("bridge_script_path", bridge_script_path))
		var port := int(info.get("bridge_port", bridge_port))
		_add_bridge_cleanup_candidate(candidates, script, port)
	for key in candidates.keys():
		var item: Dictionary = candidates[key]
		_force_stop_orphan_bridge_processes(str(item.get("script", "")), int(item.get("port", 0)))


func _add_bridge_cleanup_candidate(candidates: Dictionary, script_path: String, port: int) -> void:
	var script := _globalize(script_path)
	var script_name := script.replace("\\", "/").get_file()
	if script_name.is_empty() or port <= 0:
		return
	var key := "%s|%d" % [script_name, port]
	candidates[key] = {
		"script": script,
		"port": port,
	}


func _force_stop_orphan_bridge_processes(selected_script: String, selected_port: int) -> void:
	if not cleanup_orphan_runtime_processes:
		return
	if OS.get_name() != "Windows":
		return
	var script_name := selected_script.replace("\\", "/").get_file()
	if script_name.is_empty() or selected_port <= 0:
		return
	var command := "$scriptName = '%s'; $port = %d; $portPattern = '--port\\s+[\"'']?' + [Regex]::Escape([string]$port) + '[\"'']?'; $namePattern = '*' + $scriptName + '*'; foreach ($endpoint in @('/api/vision/stop','/api/stop_voice_chat','/api/stop')) { try { Invoke-WebRequest -UseBasicParsing -Method Post -Uri ('http://127.0.0.1:' + $port + $endpoint) -ContentType 'application/json' -Body '{}' -TimeoutSec 1 | Out-Null } catch {} }; Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^pythonw?(\\.exe)?$' -and $_.CommandLine -and $_.CommandLine -like $namePattern -and $_.CommandLine -match $portPattern } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }" % [
		_powershell_single_quote(script_name),
		selected_port,
	]
	var output: Array = []
	var exit_code := OS.execute(
		"powershell.exe",
		PackedStringArray([
			"-NoProfile",
			"-ExecutionPolicy",
			"Bypass",
			"-Command",
			command
		]),
		output,
		true,
		false
	)
	var output_text := "\n".join(output)
	if exit_code != 0 or not output_text.strip_edges().is_empty():
		push_warning("Voice bridge cleanup script=%s port=%d exit=%d output=%s" % [script_name, selected_port, exit_code, output_text])


func _powershell_single_quote(text: String) -> String:
	return text.replace("'", "''")


func _globalize(path: String) -> String:
	if path.begins_with("res://") or path.begins_with("user://"):
		return ProjectSettings.globalize_path(path)
	return path


func _set_status(text: String) -> void:
	print(text)
	if _status_label != null:
		_status_label.text = text


func _post_bridge_json(path: String, success_text: String) -> void:
	_post_bridge_json_body(path, {}, success_text)


func _post_bridge_json_body(path: String, payload: Dictionary, success_text: String) -> void:
	var request := HTTPRequest.new()
	add_child(request)
	request.timeout = 4.0
	request.request_completed.connect(
		func(_result: int, response_code: int, _headers: PackedStringArray, body: PackedByteArray) -> void:
			var text := body.get_string_from_utf8()
			if response_code >= 200 and response_code < 300:
				_set_status(success_text)
			else:
				_set_status("Bridge request failed %s: %s" % [str(response_code), text])
			request.queue_free()
	)
	var url := "http://127.0.0.1:%d%s" % [_active_bridge_port(), path]
	var err := request.request(
		url,
		PackedStringArray(["Content-Type: application/json"]),
		HTTPClient.METHOD_POST,
		JSON.stringify(payload)
	)
	if err != OK:
		_set_status("Bridge request could not start: %s" % str(err))
		request.queue_free()


func _post_bridge_json_async(path: String, success_text: String) -> bool:
	if not is_inside_tree():
		return false
	var request := HTTPRequest.new()
	add_child(request)
	request.timeout = 4.0
	var url := "http://127.0.0.1:%d%s" % [_active_bridge_port(), path]
	var err := request.request(
		url,
		PackedStringArray(["Content-Type: application/json"]),
		HTTPClient.METHOD_POST,
		"{}"
	)
	if err != OK:
		_set_status("Bridge request could not start: %s" % str(err))
		request.queue_free()
		return false

	var completed: Array = await request.request_completed
	var response_code := int(completed[1])
	var body := completed[3] as PackedByteArray
	var text := body.get_string_from_utf8()
	request.queue_free()
	if response_code >= 200 and response_code < 300:
		_set_status(success_text)
		return true
	_set_status("Bridge request failed %s: %s" % [str(response_code), text])
	return false
