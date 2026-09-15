extends SpineSprite

@onready var touch_bone_node: SpineBoneNode = get_node_or_null("SpineBoneNode")
@onready var hitbox: Area2D = $"../Area2D"

var holding: bool = false
var start_mouse_x: float = 0.0
var target_x: float = 0.0
var current_x: float = 0.0
var initial_pos: Vector2 = Vector2.ZERO

const MAX_X: float = 80.0
const DRAG_RANGE_X: float = 120.0
const SMOOTH_X: float = 0.1

# --- TIMER AUTO-SLEEP ---
const SLEEP_AFTER_SECONDS: float = 10.0
var idle_timer: float = 0.0
var is_sleeping: bool = false

func _ready():
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_BORDERLESS, true)
	process_mode = Node.PROCESS_MODE_ALWAYS
	
	# Pastikan Area2D (hitbox) selalu mendengarkan klik meskipun sedang PAUSED!
	if hitbox:
		hitbox.process_mode = Node.PROCESS_MODE_ALWAYS

	var state = get_animation_state()
	if state:
		state.set_animation("Idle_01", true, 0)

	if touch_bone_node:
		initial_pos = touch_bone_node.position

	wake_up()

func _process(delta: float):
	if is_sleeping:
		return

	if not holding:
		idle_timer += delta
		if idle_timer >= SLEEP_AFTER_SECONDS:
			go_to_sleep()
			return

	if not touch_bone_node:
		return

	if holding:
		current_x = lerp(current_x, target_x, SMOOTH_X)
		touch_bone_node.position = initial_pos + Vector2(current_x, 0)
	elif abs(current_x) > 0.1:
		current_x = lerp(current_x, 0.0, SMOOTH_X)
		touch_bone_node.position = initial_pos + Vector2(current_x, 0)
	elif current_x != 0.0:
		current_x = 0.0
		touch_bone_node.position = initial_pos

# --- FUNGSI TIDUR ---
func go_to_sleep():
	if is_sleeping:
		return
	is_sleeping = true
	# Pause scene tree
	get_tree().paused = true
	# Kunci engine ke 1 FPS
	Engine.max_fps = 1

# --- FUNGSI BANGUN ---
func wake_up():
	idle_timer = 0.0
	is_sleeping = false
	get_tree().paused = false
	Engine.max_fps = 60

# Gunakan _unhandled_input agar klik mouse SELALU masuk tanpa terblokir sleep
func _unhandled_input(event: InputEvent):
	if event is InputEventMouseButton:
		if event.button_index == MOUSE_BUTTON_LEFT:
			if event.pressed:
				# Cek apakah klik berada di dalam area dahi
				var mouse_pos = get_viewport().get_mouse_position()
				if hitbox and is_point_inside_hitbox(mouse_pos):
					start_pat(mouse_pos.x)
			else:
				if holding:
					stop_pat()

	elif event is InputEventMouseMotion and holding:
		wake_up()
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
	wake_up()
	if holding:
		return
	holding = true
	start_mouse_x = mouse_x
	target_x = 0.0
	
	var state = get_animation_state()
	if state:
		state.set_animation("Pat_01_M", false, 1)
		state.add_animation("Pat_02_M", false, 0.0, 1)

func stop_pat():
	if not holding:
		return
	holding = false
	target_x = 0.0
	idle_timer = 0.0
	
	var state = get_animation_state()
	if state:
		state.set_animation("PatEnd_01_M", false, 1)
		state.add_empty_animation(1, 0.25, 0.0)

func _on_area_2d_input_event(viewport: Node, event: InputEvent, shape_idx: int):
	if event is InputEventMouseButton:
		if event.button_index == MOUSE_BUTTON_LEFT and event.pressed:
			start_pat(event.position.x)
