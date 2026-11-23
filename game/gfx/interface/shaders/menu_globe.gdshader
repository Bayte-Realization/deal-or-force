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
uniform vec3 air_colour : source_color = vec3(0.32, 0.50, 0.76);
uniform vec3 lamp_colour : source_color = vec3(0.95, 0.89, 0.80);
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
	vec3 view_dir = normalize(VIEW);
	vec3 sun_dir = normalize(sun_direction);
	float lit = dot(facing_normal, sun_dir);
	float day = smoothstep(-0.10, 0.78, lit);
	// Subtle dusk/dawn Rayleigh twilight band along the planetary terminator.
	float twilight = smoothstep(-0.18, 0.04, lit) * (1.0 - smoothstep(0.04, 0.34, lit));
	vec3 colour = surface * (0.034 + 0.96 * day);
	colour += surface * vec3(0.98, 0.56, 0.28) * twilight * 0.12;
	// Soft sunlit ocean/orbital specular hint for planetary depth (unshaded, mobile-safe).
	vec3 half_sum = view_dir + sun_dir;
	vec3 half_vec = half_sum / max(length(half_sum), 0.0001);
	float ocean_hint = clamp(1.0 - dot(surface, vec3(0.55, 0.40, 0.15)) * 2.2, 0.0, 1.0);
	float sun_glint = pow(clamp(dot(facing_normal, half_vec), 0.0, 1.0), 18.0) * ocean_hint * day;
	colour += vec3(0.76, 0.86, 0.98) * sun_glint * 0.14;
	// The cities come on as the ground goes dark, with a warm golden core glow.
	float dark = clamp(-lit * 3.0 + 0.24, 0.0, 1.0);
	colour += (lamps * 0.54 + lamps * lamps * 0.36) * dark * lamp_colour;
	// Air: two-stage Fresnel limb (broad azure scatter + razor-sharp sunlit horizon rim).
	float ndotv = clamp(dot(facing_normal, view_dir), 0.0, 1.0);
	float rim_broad = pow(1.0 - ndotv, 3.6);
	float rim_sharp = pow(1.0 - ndotv, 6.2);
	float limb_sun = clamp(lit + 0.22, 0.0, 1.0);
	vec3 limb_tint = mix(air_colour, vec3(0.78, 0.89, 1.0), rim_sharp * 0.55);
	limb_tint = mix(limb_tint, vec3(0.96, 0.64, 0.38), twilight * 0.35);
	colour += (rim_broad * 0.18 + rim_sharp * 0.24) * limb_tint * (0.06 + 0.94 * limb_sun);
	ALBEDO = colour * brightness;
}
