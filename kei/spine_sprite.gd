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

# --- TIMER AUTO-SLEEP (0% CPU) ---
const SLEEP_AFTER_SECONDS: float = 10.0
var idle_timer: float = 0.0
var is_sleeping: bool = false

func _ready():
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_BORDERLESS, true)
	process_mode = Node.PROCESS_MODE_ALWAYS
	
	if hitbox:
		hitbox.process_mode = Node.PROCESS_MODE_ALWAYS

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

	wake_up()

func _process(delta: float):
	if is_sleeping:
		return

	# Inactivity timer
	if not holding:
		idle_timer += delta
		if idle_timer >= SLEEP_AFTER_SECONDS:
			go_to_sleep()
			return

	if not touch_bone_node:
		return

	# Interpolasi pergerakan kepala
	if holding:
		current_x = lerp(current_x, target_x, SMOOTH_X)
		touch_bone_node.position = initial_pos + Vector2(current_x, 0)
	elif abs(current_x) > 0.1:
		current_x = lerp(current_x, 0.0, SMOOTH_X)
		touch_bone_node.position = initial_pos + Vector2(current_x, 0)
	elif current_x != 0.0:
		current_x = 0.0
		touch_bone_node.position = initial_pos

func go_to_sleep():
	if is_sleeping:
		return
	is_sleeping = true
	
	# Bekukan animasi Spine total
	if has_method("get_animation_state"):
		var state = get_animation_state()
		if state:
			state.set_time_scale(0.0)
	
	# Matikan kalkulasi _process di SpineSprite
	set_process(false)
	
	# Pause scene tree & turunkan render engine
	get_tree().paused = true
	Engine.max_fps = 1

func wake_up():
	idle_timer = 0.0
	if not is_sleeping and Engine.max_fps == 60:
		return
	is_sleeping = false
	
	# Kembalikan waktu animasi Spine normal
	if has_method("get_animation_state"):
		var state = get_animation_state()
		if state:
			state.set_time_scale(1.0)
			
	# Hidupkan kembali kalkulasi _process
	set_process(true)
	
	get_tree().paused = false
	Engine.max_fps = 60

func _unhandled_input(event: InputEvent):
	if event is InputEventMouseButton:
		if event.button_index == MOUSE_BUTTON_LEFT:
			if event.pressed:
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
	
	if has_method("get_animation_state"):
		var state = get_animation_state()
		if state:
			# Bersihkan track 1 terlebih dahulu
			state.clear_track(1)
			# Mainkan Pat_01_M (mata tertutup senang saat dielus)
			state.set_animation("Pat_01_M", false, 1)

func stop_pat():
	if not holding:
		return
	holding = false
	target_x = 0.0
	idle_timer = 0.0
	
	if has_method("get_animation_state"):
		var state = get_animation_state()
		if state:
			# Mainkan PatEnd_01_M langsung tanpa fade out campuran
			state.set_animation("PatEnd_01_M", false, 1)
			# Begitu PatEnd_01_M selesai durasinya, langsung kosongkan track 1 tanpa crossfade
			state.add_empty_animation(1, 0.0, 0.0)

func _on_area_2d_input_event(viewport: Node, event: InputEvent, shape_idx: int):
	if event is InputEventMouseButton:
		if event.button_index == MOUSE_BUTTON_LEFT and event.pressed:
			start_pat(event.position.x)
