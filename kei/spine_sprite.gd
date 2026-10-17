extends SpineSprite

@onready var touch_bone_node: SpineBoneNode = get_node_or_null("SpineBoneNode")
@onready var eye_bone_node: SpineBoneNode = get_node_or_null("SpineBoneNode2")
@onready var hitbox: Area2D = $"../Area2D"

var holding: bool = false
var start_mouse_x: float = 0.0
var target_x: float = 0.0
var current_x: float = 0.0
var initial_pos: Vector2 = Vector2.ZERO

const MAX_X: float = 80.0
const DRAG_RANGE_X: float = 120.0
const SMOOTH_X: float = 0.1

# Eye tracking variables
var initial_eye_pos: Vector2 = Vector2.ZERO
var current_eye_offset: Vector2 = Vector2.ZERO
var mouse_down: bool = false

# Konfigurasi batas jangkauan lirikan mata Kei
const MAX_EYE_RIGHT: float = 120.0   # Limit lirikan ke kanan (lebih banyak / jauh)
const MAX_EYE_LEFT: float = 60.0    # Limit lirikan ke kiri
const MAX_EYE_OFFSET_Y: float = 50.0 # Limit lirikan atas-bawah
const EYE_SMOOTH: float = 0.08
const EYE_CENTER_OFFSET_X: float = 0.0

func _ready():
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_BORDERLESS, true)

	if has_method("get_animation_state"):
		var state = get_animation_state()
		if state:
			var skel_res = get_skeleton_data_res()
			if skel_res and skel_res.has_method("get_animation_state_data"):
				var data = skel_res.get_animation_state_data()
				if data:
					# Mix halus antar transisi animasi kepala & mata
					data.set_default_mix(0.2)
					data.set_mix("Pat_01_M", "PatEnd_01_M", 0.15)
					data.set_mix("PatEnd_01_M", "Idle_01", 0.25)
			
			state.set_animation("Idle_01", true, 0)

	if touch_bone_node:
		initial_pos = touch_bone_node.position
	
	if not eye_bone_node:
		eye_bone_node = get_node_or_null("EyeBoneNode")
	if eye_bone_node:
		initial_eye_pos = eye_bone_node.position

func _process(_delta: float):
	# 1. Tracking Elus Kepala (Head Movement)
	if touch_bone_node:
		if holding:
			current_x = lerp(current_x, target_x, SMOOTH_X)
			touch_bone_node.position = initial_pos + Vector2(current_x, 0)
		elif abs(current_x) > 0.1:
			current_x = lerp(current_x, 0.0, SMOOTH_X)
			touch_bone_node.position = initial_pos + Vector2(current_x, 0)
		elif current_x != 0.0:
			current_x = 0.0
			touch_bone_node.position = initial_pos

	# 2. Tracking Bola Mata Mengikuti Kursor (Hanya saat mouse ditahan)
	if eye_bone_node:
		if not mouse_down or holding:
			current_eye_offset = lerp(current_eye_offset, Vector2.ZERO, EYE_SMOOTH)
			eye_bone_node.position = initial_eye_pos + current_eye_offset
		else:
			var vp = get_viewport()
			if vp:
				var mouse_pos = vp.get_mouse_position()
				var eye_center = eye_bone_node.global_position + Vector2(EYE_CENTER_OFFSET_X, 0.0)
				var screen_dir = (mouse_pos - eye_center)
				
				var nx = clamp(screen_dir.x / 400.0, -1.0, 1.0)
				var ny = clamp(screen_dir.y / 300.0, -1.0, 1.0)
				
				# Batas lirikan horizontal asimetris (kanan lebih banyak daripada kiri)
				var target_x_offset: float = 0.0
				if nx >= 0.0:
					target_x_offset = nx * MAX_EYE_RIGHT
				else:
					target_x_offset = nx * MAX_EYE_LEFT
				
				var target_eye = Vector2(
					target_x_offset,
					ny * MAX_EYE_OFFSET_Y
				)
				
				current_eye_offset = lerp(current_eye_offset, target_eye, EYE_SMOOTH)
				eye_bone_node.position = initial_eye_pos + current_eye_offset

func _unhandled_input(event: InputEvent):
	if event is InputEventMouseButton:
		if event.button_index == MOUSE_BUTTON_LEFT:
			mouse_down = event.pressed
			if event.pressed:
				var mouse_pos = get_viewport().get_mouse_position()
				if hitbox and is_point_inside_hitbox(mouse_pos):
					start_pat(mouse_pos.x)
			else:
				if holding:
					stop_pat()

	elif event is InputEventMouseMotion and holding:
		var dx = event.position.x - start_mouse_x
		var t = clamp(dx / DRAG_RANGE_X, -1.0, 1.0)
		target_x = t * MAX_X

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

func start_pat(mouse_x: float):
	if holding:
		return
	holding = true
	start_mouse_x = mouse_x
	target_x = 0.0
	
	if has_method("get_animation_state"):
		var state = get_animation_state()
		if state:
			state.clear_track(1)
			state.set_animation("Pat_01_M", false, 1)

func stop_pat():
	if not holding:
		return
	holding = false
	target_x = 0.0
	
	if has_method("get_animation_state"):
		var state = get_animation_state()
		if state:
			state.set_animation("PatEnd_01_M", false, 1)
			state.add_empty_animation(1, 0.0, 0.0)

func _on_area_2d_input_event(viewport: Node, event: InputEvent, shape_idx: int):
	if event is InputEventMouseButton:
		if event.button_index == MOUSE_BUTTON_LEFT:
			mouse_down = event.pressed
			if event.pressed:
				start_pat(event.position.x)
			elif holding:
				stop_pat()
