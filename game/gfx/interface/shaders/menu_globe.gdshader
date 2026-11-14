shader_type spatial;
render_mode unshaded, cull_back;

// The planet behind the main menu: one picture of the world, lit by hand.
//
// Unshaded on purpose. The engine's lights would cost a light pass and shadows for a ball
// that needs neither; the day and the night are six lines here, and the same six lines run
// on a phone. `world_map` is baked from the game's own data by Tools/bake-menu-globe.py, so
// what turns behind the menu is the world the game is played on.

uniform sampler2D world_map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D night_lights : filter_linear_mipmap, repeat_enable;
// Where the sun stands, in the camera's own space: the camera does not turn, so the light
// stays put while the planet turns under it and the dusk line crosses the continents.
uniform vec3 sun_direction = vec3(-0.76, 0.32, 0.42);
uniform vec3 air_colour : source_color = vec3(0.22, 0.42, 0.78);
uniform vec3 lamp_colour : source_color = vec3(1.0, 0.84, 0.60);
uniform float brightness = 1.0;

varying vec3 local_normal;

void vertex() {
	local_normal = normalize(VERTEX);
}

void fragment() {
	// Longitude and latitude from the point on the ball itself, not from the mesh's own
	// coordinates: the picture's middle (Greenwich) faces +Z whatever mesh is used.
	vec3 n = normalize(local_normal);
	vec2 uv = vec2(atan(n.x, n.z) / TAU + 0.5, 0.5 - asin(clamp(n.y, -1.0, 1.0)) / PI);
	// Longitude jumps from one to nought at the date line, and a derivative taken across
	// that jump says "the whole world in one pixel", which picks the smallest mip and draws
	// a seam from pole to pole. The jump is exactly one, so it is taken back out.
	vec2 across = dFdx(uv);
	vec2 down = dFdy(uv);
	across.x -= round(across.x);
	down.x -= round(down.x);
	vec3 surface = textureGrad(world_map, uv, across, down).rgb;
	float lamps = textureGrad(night_lights, uv, across, down).r;

	vec3 facing_normal = normalize(NORMAL);
	float lit = dot(facing_normal, normalize(sun_direction));
	float day = pow(clamp(lit * 1.7 + 0.06, 0.0, 1.0), 0.8);
	vec3 colour = surface * (0.022 + 1.12 * day);
	// The cities come on as the ground goes dark, a little before full night.
	float dark = clamp(-lit * 3.0 + 0.25, 0.0, 1.0);
	colour += lamps * dark * lamp_colour * 0.62;
	// Air: the edge of the ball is looked at through more of it.
	float rim = pow(1.0 - clamp(dot(facing_normal, VIEW), 0.0, 1.0), 3.2);
	colour += rim * air_colour * (0.25 + 0.9 * clamp(lit + 0.35, 0.0, 1.0));
	ALBEDO = colour * brightness;
}
