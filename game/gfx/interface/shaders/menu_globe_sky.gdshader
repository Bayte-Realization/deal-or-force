shader_type spatial;
render_mode unshaded, cull_front, depth_draw_never;

// The dark behind the menu's planet, and a few hundred stars in it. Drawn, not loaded: a
// star field is a grid of cells of which one in a hundred and twenty holds a point, and that
// is cheaper than a picture of one and is sharp on any screen. The cells are small - a
// quarter of a degree - because a star is a point: at the first size tried they were discs.

uniform vec3 ground_colour : source_color = vec3(0.0035, 0.0058, 0.0105);
uniform float star_strength = 0.9;

float cell_hash(vec2 cell) {
	return fract(sin(dot(cell, vec2(127.1, 311.7))) * 43758.5453);
}

void fragment() {
	vec2 grid = UV * vec2(1400.0, 700.0);
	vec2 cell = floor(grid);
	float chance = cell_hash(cell);
	vec2 place = vec2(cell_hash(cell + 17.0), cell_hash(cell + 41.0)) * 0.6 + 0.2;
	float point = smoothstep(0.2, 0.0, length(fract(grid) - place));
	float brightness = cell_hash(cell + 3.0);
	float star = step(0.9917, chance) * point * (0.12 + 0.88 * brightness * brightness);
	// Fewer near the poles of the shell, where the cells are squeezed into slivers.
	star *= smoothstep(0.04, 0.18, UV.y) * smoothstep(0.96, 0.82, UV.y);
	ALBEDO = ground_colour + vec3(0.82, 0.88, 1.0) * star * star_strength;
}
