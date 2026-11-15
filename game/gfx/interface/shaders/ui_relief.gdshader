shader_type canvas_item;
render_mode unshaded;

// One light, one emboss, one tone. Used only where a nine-slice cannot go: interface icons
// (a family tone and a raised edge), the flag shield, round buttons and the minimap frame.
//
// Nothing here moves or reads the screen behind it, and there are no loops: the cost is two
// extra texture reads per pixel of the control it sits on, in the Compatibility renderer and on
// phones alike. The light is not written in this file. MaterialStyles sets light_dir from the
// `lighting.direction` of game/interface/10_materials.json, so a skin that moves the light moves
// it here and on every baked panel at once. Until then the relief is flat.

uniform vec2 light_dir = vec2(0.0, 0.0);
uniform float relief : hint_range(0.0, 1.0) = 0.0;
uniform vec4 tint : source_color = vec4(1.0, 1.0, 1.0, 1.0);

void fragment() {
	vec2 offset = light_dir * TEXTURE_PIXEL_SIZE;
	float toward = texture(TEXTURE, UV + offset).a;
	float away = texture(TEXTURE, UV - offset).a;
	// Positive on edges that face the light, negative on edges turned away from it.
	float edge = (away - toward) * relief;
	vec3 colour = COLOR.rgb * tint.rgb;
	colour = mix(colour, vec3(1.0), clamp(edge, 0.0, 1.0) * 0.6);
	colour = mix(colour, vec3(0.0), clamp(-edge, 0.0, 1.0) * 0.6);
	COLOR = vec4(colour, COLOR.a * tint.a);
}
