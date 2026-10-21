extends SpineSprite

@onready var touch_bone_node: SpineBoneNode = get_node_or_null("SpineBoneNode")
@onready var eye_bone_node: SpineBoneNode = get_node_or_null("SpineBoneNode2")
@onready var hitbox: Area2D = $"../Area2D"
@onready var bgm_player: AudioStreamPlayer2D = $"../AudioStreamPlayer2D"
@onready var mute_button: Button = $"../CanvasLayer/MuteButton"

var holding: bool = false
var start_mouse_pos: Vector2 = Vector2.ZERO
var target_offset: Vector2 = Vector2.ZERO
var current_offset: Vector2 = Vector2.ZERO
var initial_pos: Vector2 = Vector2.ZERO

# --- VARIABEL EYE TRACKING ---
var initial_eye_pos: Vector2 = Vector2.ZERO
var current_eye_offset: Vector2 = Vector2.ZERO
var mouse_down: bool = false

# Batas jangkauan lirikan mata
const MAX_EYE_RIGHT: float = 70.0
const MAX_EYE_LEFT: float = 70.0
const MAX_EYE_OFFSET_Y: float = 50.0
const EYE_SMOOTH: float = 0.08
const EYE_CENTER_OFFSET_X: float = 0.0

const MAX_DRAG: float = 75.0
const DRAG_RANGE: float = 120.0
const SMOOTH_FACTOR: float = 0.12

# Vektor arah diagonal untuk kemiringan kepala Mari (\)
const PAT_DIRECTION: Vector2 = Vector2(0.85, 0.52)

func _ready():
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_BORDERLESS, true)
	process_mode = Node.PROCESS_MODE_ALWAYS
	
	if hitbox:
		hitbox.process_mode = Node.PROCESS_MODE_ALWAYS

	# Setup Audio & Mute Button
	if bgm_player:
		bgm_player.process_mode = Node.PROCESS_MODE_ALWAYS
		bgm_player.finished.connect(_on_bgm_finished)

	if mute_button:
		mute_button.process_mode = Node.PROCESS_MODE_ALWAYS
		mute_button.pressed.connect(_on_mute_button_pressed)
		mute_button.text = "🔊" if (bgm_player and bgm_player.playing) else "🔇"

	var state = get_animation_state()
	if state:
		var skel_res = get_skeleton_data_res()
		if skel_res and skel_res.has_method("get_animation_state_data"):
			var data = skel_res.get_animation_state_data()
			if data:
				data.set_default_mix(0.2)
				data.set_mix("Pat_01_M", "PatEnd_01_M", 0.15)
				data.set_mix("PatEnd_01_M", "Idle_01", 0.25)
				data.set_mix("Pat_01_A", "PatEnd_01_A", 0.15)
				data.set_mix("PatEnd_01_A", "Idle_01", 0.25)

		state.set_animation("Idle_01", true, 0)

	if touch_bone_node:
		initial_pos = touch_bone_node.position

	# Inisialisasi posisi awal bone mata
	if not eye_bone_node:
		eye_bone_node = get_node_or_null("EyeBoneNode")
	if eye_bone_node:
		initial_eye_pos = eye_bone_node.position

func _process(_delta: float):
	# 1. Tracking Elus Kepala (Head Movement)
	if touch_bone_node:
		if holding:
			current_offset = current_offset.lerp(target_offset, SMOOTH_FACTOR)
			touch_bone_node.position = initial_pos + current_offset
		elif current_offset.length() > 0.1:
			current_offset = current_offset.lerp(Vector2.ZERO, SMOOTH_FACTOR)
			touch_bone_node.position = initial_pos + current_offset
		elif current_offset != Vector2.ZERO:
			current_offset = Vector2.ZERO
			touch_bone_node.position = initial_pos

	# 2. Tracking Bola Mata Mengikuti Kursor
	if eye_bone_node:
		# Jika sedang di-pat (mata tutup) atau mouse tidak ditekan: mata kembali ke posisi tengah
		if holding or not mouse_down:
			current_eye_offset = current_eye_offset.lerp(Vector2.ZERO, EYE_SMOOTH)
			eye_bone_node.position = initial_eye_pos + current_eye_offset
		else:
			var vp = get_viewport()
			if vp:
				var mouse_pos = vp.get_mouse_position()
				var eye_center = eye_bone_node.global_position + Vector2(EYE_CENTER_OFFSET_X, 0.0)
				var screen_dir = mouse_pos - eye_center

				var nx = clamp(screen_dir.x / 400.0, -1.0, 1.0)
				var ny = clamp(screen_dir.y / 300.0, -1.0, 1.0)

				var target_x_offset: float = nx * (MAX_EYE_RIGHT if nx >= 0.0 else MAX_EYE_LEFT)
				var target_eye = Vector2(target_x_offset, ny * MAX_EYE_OFFSET_Y)

				current_eye_offset = current_eye_offset.lerp(target_eye, EYE_SMOOTH)
				eye_bone_node.position = initial_eye_pos + current_eye_offset

# --- LOGIKA TOGGLE AUDIO & LOOP ---
var is_bgm_enabled: bool = false

func _on_bgm_finished():
	# Jika musik selesai dan statusnya masih ON/Unmuted, putar ulang dari awal (looping selamanya)
	if is_bgm_enabled and bgm_player:
		bgm_player.play()

func _on_mute_button_pressed():
	if not bgm_player:
		return

	if bgm_player.playing or is_bgm_enabled:
		is_bgm_enabled = false
		bgm_player.stop()
		if mute_button:
			mute_button.text = "🔇"
	else:
		is_bgm_enabled = true
		bgm_player.play()
		if mute_button:
			mute_button.text = "🔊"

func _unhandled_input(event: InputEvent):
	if event is InputEventMouseButton:
		if event.button_index == MOUSE_BUTTON_LEFT:
			mouse_down = event.pressed
			if event.pressed:
				var mouse_pos = get_viewport().get_mouse_position()

				# Cek klik tombol Mute
				if mute_button and is_point_inside_button(mouse_pos, mute_button):
					_on_mute_button_pressed()
					return

				# Cek klik dahi untuk headpat
				if hitbox and is_point_inside_hitbox(mouse_pos):
					start_pat(mouse_pos)
			else:
				if holding:
					stop_pat()

	elif event is InputEventMouseMotion:
		if holding:
			var delta_mouse = event.position - start_mouse_pos
			var dir_normalized = PAT_DIRECTION.normalized()
			var projection = delta_mouse.dot(dir_normalized)
			var t = clamp(projection / DRAG_RANGE, -1.0, 1.0)
			target_offset = dir_normalized * (t * MAX_DRAG)

func is_point_inside_button(point: Vector2, btn: Button) -> bool:
	return btn.get_global_rect().has_point(point)

func is_point_inside_hitbox(point: Vector2) -> bool:
	if not hitbox:
		return false
	var col = hitbox.get_node_or_null("CollisionShape2D")
	if not col or not col.shape is RectangleShape2D:
		return false
	var rect_shape: RectangleShape2D = col.shape
	var half_size = rect_shape.size * 0.5
	var center = col.global_position
	var rect = Rect2(center - half_size, rect_shape.size)
	return rect.has_point(point)

func start_pat(mouse_pos: Vector2):
	if holding:
		return
	holding = true
	start_mouse_pos = mouse_pos
	target_offset = Vector2.ZERO
	
	var state = get_animation_state()
	if state:
		var entry_m = state.set_animation("Pat_01_M", false, 1)
		if entry_m:
			entry_m.set_mix_duration(0.15)

		var entry_a = state.set_animation("Pat_01_A", false, 2)
		if entry_a:
			entry_a.set_mix_duration(0.3)

func stop_pat():
	if not holding:
		return
	holding = false
	target_offset = Vector2.ZERO

	var state = get_animation_state()
	if state:
		state.set_animation("PatEnd_01_M", false, 1)
		state.add_empty_animation(1, 0.25, 0.0)

		state.set_animation("PatEnd_01_A", false, 2)
		state.add_empty_animation(2, 0.25, 0.0)

func _on_area_2d_input_event(viewport: Node, event: InputEvent, shape_idx: int):
	if event is InputEventMouseButton:
		if event.button_index == MOUSE_BUTTON_LEFT and event.pressed:
			start_pat(event.position)
