shader_type spatial;
render_mode unshaded, cull_front, blend_add, depth_draw_never;

// The air outside the limb of the menu's planet: a shell a little larger than the ball,
// drawn from the inside and added to what is behind it. How bright a point of it is depends
// only on how close the line of sight passes to the planet, so the glow hugs the edge and is
// gone a third of a unit out - and on which side the sun is, so the night limb stays dark.

uniform vec3 sun_direction = vec3(-0.76, 0.32, 0.42);
uniform vec3 air_colour : source_color = vec3(0.32, 0.50, 0.76);
uniform float planet_radius = 5.0;
uniform float thickness = 0.11;
uniform float strength = 0.14;

varying vec3 centre;

void vertex() {
	centre = (VIEW_MATRIX * MODEL_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
}

void fragment() {
	vec3 sight = normalize(VERTEX);
	vec3 nearest = sight * dot(centre, sight);
	vec3 out_from_centre = nearest - centre;
	float miss = length(out_from_centre);
	float glow = pow(clamp(1.0 - (miss - planet_radius) / thickness, 0.0, 1.0), 3.8) * step(planet_radius, miss);
	float sunward = clamp(dot(out_from_centre / max(miss, 0.0001), normalize(sun_direction)) + 0.35, 0.0, 1.0);
	ALBEDO = air_colour * glow * sunward * strength;
}
