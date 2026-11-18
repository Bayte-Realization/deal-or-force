shader_type spatial;
render_mode diffuse_burley, cull_back;
uniform sampler2D political_map : source_color, filter_linear_mipmap_anisotropic;
uniform sampler2D region_ids : filter_nearest;
uniform sampler2D province_ids : filter_nearest;
uniform sampler2D nation_ids : filter_nearest;
uniform sampler2D hydrology_map : filter_linear, repeat_enable;
// Where the nearest river's real course lies from each texel (RG, 16ths of a texel from 128) and
// that river's importance (B, 0 = none near). Read texel by texel, never filtered.
uniform sampler2D river_centre_map : filter_nearest, repeat_enable;
uniform sampler2D crossover_excavations : filter_nearest, repeat_enable;
uniform float crossover_excavations_active = 0.0;
uniform sampler2D lake_surface : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D river_surface : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float river_inset = -0.10;
uniform float river_curve_filter = 1.0;
uniform float river_near_opacity = 0.92;
uniform float river_far_opacity = 0.58;
// River width on screen, as half-widths in pixels: a headwater's thread and a great river's line.
// Close in, a river is never wider than water that size is (river_real_half, in map texels).
uniform float river_thread_px = 0.75;
uniform float river_great_px = 1.9;
uniform float river_real_half = 0.11;
uniform float water_shore_width = 0.75;
uniform float water_depth_reach = 5.0;
uniform float water_texture_strength = 0.65;
uniform float water_repeat = 640.0;
uniform float water_ripple_strength = 0.12;
uniform float water_animation_speed = 0.0;
uniform sampler2D province_control_colors : source_color, filter_nearest, repeat_disable;
// The provinces the far plate (political_map) has not caught up with yet, in the colour it is
// about to give them (PoliticalPlate.PackCatchUp). Empty except for the moment between a
// border moving and the repainted plate arriving, and laid over the plate sample only.
uniform sampler2D province_owner_colors : source_color, filter_nearest, repeat_disable;
// Who owns each province now, for the provinces whose owner has changed since the country id
// map was drawn: owner id in r/g, and b set where the entry means anything. Read as data, so
// no source_color on it.
uniform sampler2D province_owner_ids : filter_nearest, repeat_disable;
uniform float owners_moved = 0.0;
uniform sampler2D nation_palette : source_color, filter_nearest, repeat_disable;
// The lines of the map, one style a rank (Simulation.MapBorderStyle, game/gui/77_map_borders.json).
// A rank is decided below from who holds the two sides NOW and from nothing else, so there is
// no second set of these for a border that a treaty drew. Colours are linear, widths are in
// screen pixels. border_pixels is the country line's width and the unit its cushion and the
// selection outlines are measured in.
uniform float border_pixels = 1.35;
uniform vec3 country_border_color = vec3(0.035, 0.05, 0.075);
uniform float country_border_opacity = 0.86;
// The least of its weight a country line keeps as the camera pulls back to the world view.
uniform float country_border_far_weight = 0.62;
// How far each cell is from the nearest frontier, in cells over FrontierField.Cap, smoothed and
// mipmapped (Simulation.FrontierField). From the middle zoom out the country line is drawn off
// this rather than off the per-pixel rebuild, which there is a staircase a pixel high.
uniform sampler2D frontier_field : filter_linear_mipmap, repeat_enable;
uniform float frontier_ready = 0.0;
uniform float frontier_cap = 20.0;
// The smoothed field never quite reaches nought: what it holds at the frontier itself.
uniform float frontier_offset = 0.6;
// How far from the frontier, in cells, the field still counts as standing on it. The rebuild it
// stands in for sees a block of four by four cells and never anything further, so this is the
// same reach: without it the inner cushion and glow, drawn by pixel widths meant for close in,
// spread eight cells into every country from the middle zoom out and the frontiers went dark and fat.
uniform float frontier_reach = 1.5;
uniform float region_border_pixels = 3.78;
uniform vec3 region_border_color = vec3(0.0, 0.0, 0.0);
uniform float region_border_opacity = 0.0416;
uniform float province_border_pixels = 2.0;
uniform vec3 province_border_color = vec3(0.055, 0.075, 0.09);
uniform float province_border_opacity = 0.72;
uniform vec3 province_halo_color = vec3(0.78, 0.80, 0.77);
uniform float province_border_halo = 0.15;
// Not a rank: where held ground ends inside one owner's land. A pale line over a dark key.
uniform vec3 occupation_line_color = vec3(0.92, 0.88, 0.71);
uniform vec3 occupation_key_color = vec3(0.045, 0.065, 0.08);
uniform float front_pixels = 1.6;
uniform sampler2D front_membership : filter_nearest, repeat_disable;
uniform sampler2D map_mode_colors : source_color, filter_nearest, repeat_disable;
uniform sampler2D resource_colors : source_color, filter_nearest, repeat_disable;
uniform float resource_mode = 0.0;
uniform sampler2DArray terrain_tiles : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D province_terrain : filter_nearest, repeat_disable;
uniform sampler2D terrain_surface : filter_nearest, repeat_enable;
uniform sampler2D border_plan_targets : filter_nearest, repeat_disable;
uniform sampler2D border_controller_ids : filter_nearest, repeat_disable;
uniform sampler2D relief_height : filter_linear_mipmap, repeat_enable;
uniform sampler2D relief_color : source_color, filter_linear_mipmap, repeat_enable;
// The depth of the sea (map_data/bathymetry.png): 1 at the coast and on the shelves, 0 over the abyss.
uniform sampler2D ocean_depth : filter_linear_mipmap, repeat_enable;
uniform float ocean_depth_ready = 0.0;
uniform vec3 ocean_shelf_color = vec3(0.050, 0.185, 0.225);
uniform float terrain_detail = 0.35;
// The atlas hillshade: slopes lit from the north-west, as a printed relief map lights them. It is
// worked out on the surface itself, so the flat map - which the scene's sun meets face on -
// shows its mountains as plainly as the globe does.
uniform float relief_shading = 0.9;
uniform float terrain_ready = 0.0;
uniform float terrain_mode = 0.0;
uniform float political_opacity = 0.62;
// The nation palette a little short of full strength, so the paint reads as ink over ground
// rather than as a poster: the relief under China is a country, not a yellow sheet.
uniform float political_saturation = 0.86;
// 1 on the flat map: an atlas sheet read straight on, without the globe's light and curvature
// to soften it. The terrain tiles showing through the country colours read there as blotches
// and their fine grain as noise, so the flat map wears the colours thicker and the grain thinner.
uniform float flat_projection = 0.0;
uniform float terrain_repeat = 180.0;
// From which latitude tiled textures leave the latitude-longitude grid for a plane about the
// nearer pole, and over how many degrees the two are blended (polar_plane).
uniform float polar_tiling_from = 50.0;
uniform float polar_tiling_band = 14.0;
uniform float terrain_blend_texels = 1.15;
// Small: the noise's slope must stay below the line's width, or a province line wobbles,
// splits into strands and swells into blots (0.62 did; tested live against 0 and 0.15).
uniform float id_wander_texels = 0.15;

// Whether the map is smoothed at all, as a switch the player owns.
//
// Off is not a degraded version of on, it is a different picture: the raster exactly as it
// was authored, sampled at the nearest texel, with the texel grid visible. That is a real
// preference and this project has reasons to respect it - a map whose pixels you can count
// is a map whose data a modder can see - and it is also the cheaper picture, because the two
// octaves of value noise that take the boundaries off the grid are not evaluated at all.
uniform float map_smoothing = 1.0;
uniform float terrain_warp_texels = 2.20;
uniform float relief_strength = 0.8;
uniform float relief_color_opacity = 0.0;
// Matches the source scenario's Ocean terrain (#0f4a6b). The relief texture stays
// deliberately disabled: at strategic scale it reads as visual noise, not water.
// Darkened here to compensate for Godot's directional + ambient lighting; the final
// lit result is the source scenario's #0f4a6b rather than a washed-out cyan.
uniform vec3 ocean_color = vec3(0.012, 0.065, 0.105);
uniform float selection_active = 0.0;
// The country the player last right-clicked, by map index, or -1 for none.
uniform float highlighted_nation = -1.0;
uniform float selected_region_id = -1.0;
uniform float selected_province_id = -1.0;
// The live region the player picked, province by province (SelectedRegionMembers): a region a border
// has cut or a treaty has stitched together is no longer the region the raster was drawn with.
uniform sampler2D selected_region_members : filter_nearest, repeat_disable;
uniform float selected_region_live = 0.0;
uniform float detail_mix = 0.0;
uniform float province_detail = 0.0;
uniform float map_mode = 0.0;
// The atlas look (Simulation.MapLook, game/gui/78_map_look.json): warm paper and ink, and a sheet
// of paper from far away. Every term it adds stands behind atlas_look, and both switches start at
// nought, so a material nobody has configured - and the classic setting - draws the old map.
uniform float atlas_look = 0.0;
// 0 close in, 1 where the map has become paper; worked out from the camera once a frame.
uniform float paper_mix = 0.0;
// Linear colours; the defaults are the design's starting table, the game sets them from the skin.
uniform vec3 look_paper = vec3(0.7913, 0.6939, 0.4969);
// The colours the relief leans towards: a cool shade and a warm lamp, as in Imhof's relief
// shading. Only their hue is used - the brightness of a slope is the old grey hillshade's.
uniform vec3 look_shade = vec3(0.0685, 0.0782, 0.1119);
uniform vec3 look_lamp = vec3(1.0, 0.8796, 0.6445);
uniform float look_relief_tint = 0.60;
uniform vec3 look_sea_deep = vec3(0.0091, 0.0319, 0.0497);
uniform vec3 look_sea_mid = vec3(0.0168, 0.0704, 0.0976);
uniform vec3 look_sea_shelf = vec3(0.0467, 0.1470, 0.1620);
uniform vec3 look_sea_coast = vec3(0.1441, 0.3050, 0.2789);
uniform vec3 look_sea_chart = vec3(0.0232, 0.0578, 0.0802);
uniform vec3 look_foam = vec3(0.8388, 0.7835, 0.6445);
uniform vec3 look_coast_ink = vec3(0.0273, 0.0513, 0.0648);
uniform vec3 look_unowned = vec3(0.6867, 0.6038, 0.4342);
uniform float look_ground_show_near = 0.30;
uniform float look_cover_far = 0.95;
uniform float look_saturation_far = 0.80;
uniform float look_relief_far = 0.45;
uniform float look_far_band = 0.36;
uniform float look_far_band_pixels = 4.5;
uniform float look_foam_strength = 0.55;
uniform float look_waterline = 0.0;
uniform float look_sea_sheen = 0.30;
uniform float look_selection_width = 1.3;
float map_id(sampler2D map_texture, vec2 uv) {
    vec4 encoded = texture(map_texture, vec2(fract(uv.x), clamp(uv.y, 0.0, 1.0)));
    return floor(encoded.r * 255.0 + 0.5) + floor(encoded.g * 255.0 + 0.5) * 256.0;
}
float id_at(sampler2D ids, vec2 cell, vec2 size) {
    return map_id(ids, (cell + vec2(0.5)) / size);
}
vec4 nation_paint(float nation) {
    float low = mod(nation, 256.0);
    float high = floor(nation / 256.0);
    return texture(nation_palette, (vec2(low, high) + vec2(0.5)) / 256.0);
}

/// A number between zero and one that depends only on where you are, and looks like nothing.
///
/// Two of these steer the terrain boundary off the grid it was stored on. A boundary
/// reconstructed from the raster alone is exact but straight, and a coastline of exact
/// straight segments meeting at texel corners reads as a machine drew it. Pushing the point
/// of sampling around by a texel or two first makes the same boundary wander the way ground
/// actually does, at no cost in fidelity: it is the same line, walked unevenly.
float hash21(vec2 p) {
    p = fract(p * vec2(127.31, 311.7));
    p += dot(p, p + 34.53);
    return fract(p.x * p.y);
}

float value_noise(vec2 p) {
    vec2 cell = floor(p);
    vec2 f = p - cell;
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash21(cell), hash21(cell + vec2(1.0, 0.0)), f.x),
               mix(hash21(cell + vec2(0.0, 1.0)), hash21(cell + vec2(1.0, 1.0)), f.x), f.y);
}

/// One row of atlas_noise: `period` cells round the world, so the row closes on itself at the
/// date line.
float atlas_noise_row(float u, float period, float row) {
    float x = fract(u) * period;
    float cell = floor(x);
    float f = x - cell;
    f = f * f * (3.0 - 2.0 * f);
    return mix(hash21(vec2(mod(cell, period), row)), hash21(vec2(mod(cell + 1.0, period), row)), f);
}

/// value_noise laid on the map, `across` cells round the equator, that meets itself at the date
/// line instead of drawing a seam there. On the globe a parallel at latitude L is cos(L) as long
/// as the equator, so a noise laid straight on the map would be squashed east to west into stripes
/// along the meridians near a pole. Each row of cells is given as many cells as fit round its own
/// parallel instead - a whole number, so it still closes - and the two rows either side of a point
/// are blended north to south as usual. On the flat map the parallels are as long as the equator
/// on screen, so there every row keeps the equator's count.
float atlas_noise(vec2 uv, float across, float flat_share) {
    float rows = max(round(across * 0.5), 1.0);
    float y = clamp(uv.y, 0.0, 1.0) * rows;
    float row = floor(y);
    float f = y - row;
    f = f * f * (3.0 - 2.0 * f);
    float shrink0 = mix(cos((row / rows - 0.5) * 3.1415927), 1.0, flat_share);
    float shrink1 = mix(cos(((row + 1.0) / rows - 0.5) * 3.1415927), 1.0, flat_share);
    float period0 = max(round(across * shrink0), 1.0);
    float period1 = max(round(across * shrink1), 1.0);
    return mix(atlas_noise_row(uv.x, period0, row), atlas_noise_row(uv.x, period1, row + 1.0), f);
}

/// Two octaves, centred on zero: a slow wander with a finer one riding on it.
float grain(vec2 p) {
    return value_noise(p) * 0.66 + value_noise(p * 2.7 + 11.3) * 0.34 - 0.5;
}

/// One row of the four-by-four block of ids around a cell: the column before it, its own two
/// columns, and the one after.
vec4 id_row(sampler2D ids, vec2 cell, vec2 size) {
    return vec4(id_at(ids, cell + vec2(-1.0, 0.0), size), id_at(ids, cell, size),
                id_at(ids, cell + vec2(1.0, 0.0), size), id_at(ids, cell + vec2(2.0, 0.0), size));
}

/// Which of four ids are the one asked about, as ones and zeroes.
vec4 same_id(vec4 ids, float id) {
    return step(abs(ids - vec4(id)), vec4(0.5));
}

/// How strongly one label claims a point of a cell, smoothed over the texels around it.
///
/// Each of the cell's four corners is blurred with its eight neighbours - a tent of one texel
/// either way - and the point is interpolated between the blurred corners rather than the raw
/// ones. A blur alone would also dissolve anything a texel wide, and a one-texel island or a
/// city state is exactly the ground a player most wants to see, so a corner the blur would
/// hand to its neighbours is pulled most of the way back to the label it was painted with.
float label_claim(vec4 m0, vec4 m1, vec4 m2, vec4 m3, vec2 f) {
    vec2 r0 = vec2(m0.x * 0.5 + m0.y + m0.z * 0.5, m0.y * 0.5 + m0.z + m0.w * 0.5);
    vec2 r1 = vec2(m1.x * 0.5 + m1.y + m1.z * 0.5, m1.y * 0.5 + m1.z + m1.w * 0.5);
    vec2 r2 = vec2(m2.x * 0.5 + m2.y + m2.z * 0.5, m2.y * 0.5 + m2.z + m2.w * 0.5);
    vec2 r3 = vec2(m3.x * 0.5 + m3.y + m3.z * 0.5, m3.y * 0.5 + m3.z + m3.w * 0.5);
    vec2 top = (r0 * 0.5 + r1 + r2 * 0.5) * 0.25;
    vec2 bottom = (r1 * 0.5 + r2 + r3 * 0.5) * 0.25;
    vec2 own_top = m1.yz;
    vec2 own_bottom = m2.yz;
    top = mix(top, own_top, 0.8 * mix(step(0.48, top), 1.0 - step(0.52, top), own_top));
    bottom = mix(bottom, own_bottom, 0.8 * mix(step(0.48, bottom), 1.0 - step(0.52, bottom), own_bottom));
    return mix(mix(top.x, top.y, f.x), mix(bottom.x, bottom.y, f.x), f.y);
}

/// One label's turn in the vote below: its claim is weighed only if it is not already the
/// leader or the runner-up, which with two labels in a block - nearly every block - means
/// each is weighed once however many of the sixteen texels it holds.
void weigh_label(float label, vec4 r0, vec4 r1, vec4 r2, vec4 r3, vec2 f,
                 inout float best, inout float best_id, inout float second, inout float second_id) {
    if (abs(label - best_id) < 0.5 || abs(label - second_id) < 0.5) return;
    float claim = label_claim(same_id(r0, label), same_id(r1, label), same_id(r2, label), same_id(r3, label), f);
    if (claim > best) { second = best; second_id = best_id; best = claim; best_id = label; }
    else if (claim > second) { second = claim; second_id = label; }
}

/// Boundaries recovered from the four-by-four block of ids around a cell.
///
/// The four-texel reconstruction recovers a boundary to within half a texel, which is exact
/// for a straight edge at forty-five degrees and wrong for everything else: a coast running at
/// a shallow angle was digitised as runs of four texels and a one-texel step, and half a texel
/// of reconstruction keeps every step. Close in a texel is several screen pixels, and the
/// steps are the staircase the map was criticised for.
///
/// So every label in the block makes its smoothed claim, the strongest takes the pixel, and
/// the coverage is how far it leads the runner-up. The half-way line is where the two claims
/// are equal - the coast as it ran before it was digitised, a shallow slope as a slope and a
/// round bay as a round bay. Every label in the block is asked, not only those at the cell's
/// corners: a label two texels away still has a claim here, and leaving it out makes the
/// answer jump at every cell edge, which is a staircase again by another route. With two
/// labels the lead changes by one a texel across a straight edge, as the raw coverage did,
/// so the lines drawn off it keep their width in screen pixels.
///
/// `reach` is how far away that edge is, in texels. The coverage cannot say: it is full half a
/// texel from the boundary, and every pixel beyond that read as standing half a texel off it.
/// Once a texel was a few pixels across, the shoulder and the stroke of every line reached
/// right out to the edge of the block - a pale band two texels wide down each side of each
/// province line, its outer edge the texel grid, and a grey one down every coast. The lead
/// itself keeps growing: one a texel for the first half texel, as the coverage does, then a
/// half a texel as the blur runs out, to one at a texel and a half.
float resolve_block(vec4 r0, vec4 r1, vec4 r2, vec4 r3, vec2 f, out float near_id, out float other_id, out float reach) {
    float best = -1.0;
    float best_id = -1.0;
    float second = 0.0;
    float second_id = -1.0;
    // The cell's own corners first: they are nearly always the two that matter.
    weigh_label(r1.y, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r1.z, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r2.y, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r2.z, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r0.x, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r0.y, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r0.z, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r0.w, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r1.x, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r1.w, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r2.x, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r2.w, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r3.x, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r3.y, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r3.z, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r3.w, r0, r1, r2, r3, f, best, best_id, second, second_id);
    near_id = best_id;
    other_id = second_id < -0.5 ? best_id : second_id;
    float lead = clamp(best - second, 0.0, 1.0);
    reach = lead < 0.5 ? lead : lead * 2.0 - 0.5;
    return clamp(0.5 + best - second, 0.0, 1.0);
}

/// The terrain type this point stands on, as the slot number of its ground texture.
float slot_at(sampler2D surface, vec2 cell, vec2 size) {
    vec2 uv = (cell + vec2(0.5)) / size;
    return floor(texture(surface, vec2(fract(uv.x), clamp(uv.y, 0.0, 1.0))).r * 255.0 + 0.5);
}

/// The same reconstruction the borders use, applied to which ground is underfoot.
///
/// The terrain map is an id map like any other - the number in it selects a texture, and the
/// average of two texture numbers is not a texture number - so it was read nearest and
/// rounded, and the ground changed abruptly at the edge of a texel. That is the single most
/// visible grid on the map: not a line anywhere, but every square of grass meeting every
/// square of rock, over the whole world.
vec4 slot_row(sampler2D surface, vec2 cell, vec2 size) {
    return vec4(slot_at(surface, cell + vec2(-1.0, 0.0), size), slot_at(surface, cell, size),
                slot_at(surface, cell + vec2(1.0, 0.0), size), slot_at(surface, cell + vec2(2.0, 0.0), size));
}

float terrain_coverage(sampler2D surface, vec2 uv, bool smooth_edges, out float near_slot, out float other_slot) {
    vec2 size = vec2(textureSize(surface, 0));
    vec2 g = uv * size - vec2(0.5);
    vec2 base_cell = floor(g);
    vec2 f = g - base_cell;
    if (smooth_edges) {
        float reach;
        return resolve_block(slot_row(surface, base_cell + vec2(0.0, -1.0), size), slot_row(surface, base_cell, size),
                             slot_row(surface, base_cell + vec2(0.0, 1.0), size), slot_row(surface, base_cell + vec2(0.0, 2.0), size),
                             f, near_slot, other_slot, reach);
    }

    float s00 = slot_at(surface, base_cell, size);
    float s10 = slot_at(surface, base_cell + vec2(1.0, 0.0), size);
    float s01 = slot_at(surface, base_cell + vec2(0.0, 1.0), size);
    float s11 = slot_at(surface, base_cell + vec2(1.0, 1.0), size);

    float w00 = (1.0 - f.x) * (1.0 - f.y);
    float w10 = f.x * (1.0 - f.y);
    float w01 = (1.0 - f.x) * f.y;
    float w11 = f.x * f.y;

    near_slot = f.x < 0.5 ? (f.y < 0.5 ? s00 : s01) : (f.y < 0.5 ? s10 : s11);

    float coverage = 0.0;
    coverage += abs(s00 - near_slot) < 0.5 ? w00 : 0.0;
    coverage += abs(s10 - near_slot) < 0.5 ? w10 : 0.0;
    coverage += abs(s01 - near_slot) < 0.5 ? w01 : 0.0;
    coverage += abs(s11 - near_slot) < 0.5 ? w11 : 0.0;

    other_slot = near_slot;
    float claim = 0.0;
    if (abs(s00 - near_slot) >= 0.5 && w00 > claim) { claim = w00; other_slot = s00; }
    if (abs(s10 - near_slot) >= 0.5 && w10 > claim) { claim = w10; other_slot = s10; }
    if (abs(s01 - near_slot) >= 0.5 && w01 > claim) { claim = w01; other_slot = s01; }
    if (abs(s11 - near_slot) >= 0.5 && w11 > claim) { claim = w11; other_slot = s11; }
    return coverage;
}

/// Coverage of the nearest id over this pixel, and the neighbour with the next best claim.
///
/// This is the whole of the technique. An id map sampled nearest gives a hard staircase at
/// the texel grid; sampled linearly it gives mush, because ids are labels and the average of
/// two labels is not a label. So the ids are read at the four texels around the point and
/// turned into a coverage instead: how much of this pixel belongs to the label the pixel is
/// nearest to. That is a smooth field whose half-way line is a straight edge cutting through
/// the texel grid at any angle, which is exactly the boundary the raster was drawn from -
/// recovered rather than approximated. Everything else here is reading off that one number.
float boundary_coverage(sampler2D ids, vec2 uv, bool smooth_edges, out float near_id, out float other_id, out float reach) {
    vec2 size = vec2(textureSize(ids, 0));
    vec2 grid = uv * size - vec2(0.5);
    vec2 base_cell = floor(grid);
    vec2 f = grid - base_cell;

    if (smooth_edges) {
        return resolve_block(id_row(ids, base_cell + vec2(0.0, -1.0), size), id_row(ids, base_cell, size),
                             id_row(ids, base_cell + vec2(0.0, 1.0), size), id_row(ids, base_cell + vec2(0.0, 2.0), size),
                             f, near_id, other_id, reach);
    }
    float i00 = id_at(ids, base_cell, size);
    float i10 = id_at(ids, base_cell + vec2(1.0, 0.0), size);
    float i01 = id_at(ids, base_cell + vec2(0.0, 1.0), size);
    float i11 = id_at(ids, base_cell + vec2(1.0, 1.0), size);

    float w00 = (1.0 - f.x) * (1.0 - f.y);
    float w10 = f.x * (1.0 - f.y);
    float w01 = (1.0 - f.x) * f.y;
    float w11 = f.x * f.y;

    near_id = f.x < 0.5 ? (f.y < 0.5 ? i00 : i01) : (f.y < 0.5 ? i10 : i11);

    float coverage = 0.0;
    coverage += abs(i00 - near_id) < 0.5 ? w00 : 0.0;
    coverage += abs(i10 - near_id) < 0.5 ? w10 : 0.0;
    coverage += abs(i01 - near_id) < 0.5 ? w01 : 0.0;
    coverage += abs(i11 - near_id) < 0.5 ? w11 : 0.0;

    other_id = near_id;
    float claim = 0.0;
    if (abs(i00 - near_id) >= 0.5 && w00 > claim) { claim = w00; other_id = i00; }
    if (abs(i10 - near_id) >= 0.5 && w10 > claim) { claim = w10; other_id = i10; }
    if (abs(i01 - near_id) >= 0.5 && w01 > claim) { claim = w01; other_id = i01; }
    if (abs(i11 - near_id) >= 0.5 && w11 > claim) { claim = w11; other_id = i11; }
    reach = abs(coverage - 0.5);
    return coverage;
}

/// The country a map cell belongs to NOW. The id map was drawn from the authored world and is
/// never rewritten - it is sixteen million cells - so a province a peace signed over still
/// says its old owner in it. Everything drawn off the country id (the border stroke, the
/// highlighted country, the map modes) goes through this instead, and a province whose owner
/// has moved answers with its new one.
float nation_at(vec2 cell, vec2 size) {
    float nation = id_at(nation_ids, cell, size);
    if (owners_moved < 0.5) return nation;
    float province = id_at(province_ids, cell, size);
    if (province < 0.5) return nation;
    float low = mod(province, 256.0);
    float high = floor(province / 256.0);
    vec4 moved = texture(province_owner_ids, (vec2(low, high) + vec2(0.5)) / 256.0);
    return moved.b > 0.5 ? floor(moved.r * 255.0 + 0.5) + floor(moved.g * 255.0 + 0.5) * 256.0 : nation;
}

/// boundary_coverage, for countries: the same reconstruction, read through nation_at.
vec4 nation_row(vec2 cell, vec2 size) {
    return vec4(nation_at(cell + vec2(-1.0, 0.0), size), nation_at(cell, size),
                nation_at(cell + vec2(1.0, 0.0), size), nation_at(cell + vec2(2.0, 0.0), size));
}

/// The same for countries - and, the sea being a country id like any other here, for every
/// shore on the map as well.
float nation_coverage(vec2 uv, bool smooth_edges, out float near_id, out float other_id, out float reach) {
    vec2 size = vec2(textureSize(nation_ids, 0));
    vec2 grid = uv * size - vec2(0.5);
    vec2 base_cell = floor(grid);
    vec2 f = grid - base_cell;

    if (smooth_edges) {
        return resolve_block(nation_row(base_cell + vec2(0.0, -1.0), size), nation_row(base_cell, size),
                             nation_row(base_cell + vec2(0.0, 1.0), size), nation_row(base_cell + vec2(0.0, 2.0), size),
                             f, near_id, other_id, reach);
    }
    float i00 = nation_at(base_cell, size);
    float i10 = nation_at(base_cell + vec2(1.0, 0.0), size);
    float i01 = nation_at(base_cell + vec2(0.0, 1.0), size);
    float i11 = nation_at(base_cell + vec2(1.0, 1.0), size);

    float w00 = (1.0 - f.x) * (1.0 - f.y);
    float w10 = f.x * (1.0 - f.y);
    float w01 = (1.0 - f.x) * f.y;
    float w11 = f.x * f.y;

    near_id = f.x < 0.5 ? (f.y < 0.5 ? i00 : i01) : (f.y < 0.5 ? i10 : i11);

    float coverage = 0.0;
    coverage += abs(i00 - near_id) < 0.5 ? w00 : 0.0;
    coverage += abs(i10 - near_id) < 0.5 ? w10 : 0.0;
    coverage += abs(i01 - near_id) < 0.5 ? w01 : 0.0;
    coverage += abs(i11 - near_id) < 0.5 ? w11 : 0.0;

    other_id = near_id;
    float claim = 0.0;
    if (abs(i00 - near_id) >= 0.5 && w00 > claim) { claim = w00; other_id = i00; }
    if (abs(i10 - near_id) >= 0.5 && w10 > claim) { claim = w10; other_id = i10; }
    if (abs(i01 - near_id) >= 0.5 && w01 > claim) { claim = w01; other_id = i01; }
    if (abs(i11 - near_id) >= 0.5 && w11 > claim) { claim = w11; other_id = i11; }
    reach = abs(coverage - 0.5);
    return coverage;
}

vec4 plate_catch_up(float province) {
    float low = mod(province, 256.0);
    float high = floor(province / 256.0);
    return texture(province_owner_colors, (vec2(low, high) + vec2(0.5)) / 256.0);
}
vec4 control_color(float province) {
    float low = mod(province, 256.0);
    float high = floor(province / 256.0);
    return texture(province_control_colors, (vec2(low, high) + vec2(0.5)) / 256.0);
}
float occupation_mask(vec4 control) {
    return step(0.10, control.a) * (1.0 - step(0.75, control.a));
}
vec3 occupation_ribbons(vec3 owner, vec3 controller, vec2 at, float detail, float active) {
    float phase = (at.x * 2.0 + at.y) * mix(56.0, 360.0, detail);
    float footprint = max(fwidth(phase), 0.0001);
    float distance_to_band = abs(fract(phase) - 0.5);
    // Held ground wears the colour of whoever holds it, striped with the owner's (as every
    // grand strategy draws it): a conquered country left in its own colour read as a highlight
    // pasted over the conqueror's map. From far away, where the ribbons cannot be resolved, it is
    // the holder's colour: held ground reads as the holder's from orbit, as on every strategy map.
    float visible = 1.0 - smoothstep(0.18, 0.42, footprint);
    float aa = min(footprint * 0.6, 0.055);
    float key = 1.0 - smoothstep(0.135 - aa, 0.135 + aa, distance_to_band);
    float ribbon = 1.0 - smoothstep(0.085 - aa, 0.085 + aa, distance_to_band);
    vec3 inked = mix(controller, vec3(0.055, 0.075, 0.085), key * 0.55);
    inked = mix(inked, owner, ribbon * 0.94);
    vec3 far_away = controller;
    return mix(owner, mix(far_away, inked, visible), active);
}
float lease_mask(vec4 control) {
    return step(0.76, control.a) * (1.0 - step(0.92, control.a));
}
vec3 lease_weave(vec3 owner, vec3 tenant, vec2 at, float detail, float active) {
    // A fine grid of dots in the tenant's colour over the owner's: a base on somebody's land.
    vec2 cell = at * vec2(2.0, 1.0) * mix(70.0, 420.0, detail);
    float footprint = max(fwidth(cell.x), 0.0001);
    float visible = 1.0 - smoothstep(0.20, 0.45, footprint);
    vec2 inner = fract(cell) - 0.5;
    float radius = length(inner);
    float aa = min(footprint * 0.7, 0.06);
    float dot_ = 1.0 - smoothstep(0.17 - aa, 0.17 + aa, radius);
    float rim = (1.0 - smoothstep(0.23 - aa, 0.23 + aa, radius)) - dot_;
    vec3 inked = mix(owner, vec3(0.06, 0.08, 0.09), rim * 0.55);
    inked = mix(inked, tenant * 1.05, dot_ * 0.92);
    return mix(owner, inked, active * visible);
}
float occupation_controller(float province) {
    vec2 at = (vec2(mod(province, 256.0), floor(province / 256.0)) + 0.5) / 256.0;
    vec2 code = round(texture(border_controller_ids, at).rg * 255.0);
    return code.r + code.g * 256.0;
}
vec4 map_mode_color(float nation) {
    float low = mod(nation, 256.0);
    float high = floor(nation / 256.0);
    return texture(map_mode_colors, (vec2(low, high) + vec2(0.5)) / 256.0);
}
float selected_member(float province) {
    float low = mod(province, 256.0);
    float high = floor(province / 256.0);
    return texture(selected_region_members, (vec2(low, high) + vec2(0.5)) / 256.0).r;
}
vec3 front_data(float province) {
    float low = mod(province, 256.0);
    float high = floor(province / 256.0);
    return texture(front_membership, (vec2(low, high) + vec2(0.5)) / 256.0).rgb;
}
float front_crossing(float province, vec3 here, vec2 uv) {
    float other_province = map_id(province_ids, uv);
    vec3 other = front_data(other_province);
    float both = step(0.002, here.r) * step(0.002, other.r);
    float same_war = 1.0 - step(0.002, abs(here.r - other.r));
    float opposite_sides = step(0.20, abs(here.g - other.g));
    return both * same_war * opposite_sides * step(0.5, abs(province - other_province));
}
float planned_border(float province, vec2 uv) {
    vec2 lookup = (vec2(mod(province, 256.0), floor(province / 256.0)) + 0.5) / 256.0;
    vec4 target = round(texture(border_plan_targets, lookup) * 255.0);
    float own = target.r + target.g * 256.0;
    float enemy = target.b + target.a * 256.0;
    float neighbor = map_id(province_ids, uv);
    vec2 neighbor_lookup = (vec2(mod(neighbor, 256.0), floor(neighbor / 256.0)) + 0.5) / 256.0;
    // Decode this lookup directly: map_id's repeating raster sampler is incompatible
    // with repeat_disable controller lookups in Godot's shader compiler.
    vec2 here_code = round(texture(border_controller_ids, lookup).rg * 255.0);
    vec2 other_code = round(texture(border_controller_ids, neighbor_lookup).rg * 255.0);
    float here_owner = here_code.r + here_code.g * 256.0;
    float other_owner = other_code.r + other_code.g * 256.0;
    return step(0.5, enemy) * (1.0 - step(0.5, abs(own - here_owner))) *
        (1.0 - step(0.5, abs(enemy - other_owner)));
}
uniform float fog_viewer = -1.0;
uniform sampler2D fog_visibility : filter_nearest, repeat_disable;
// How much of the tint to apply, which is a function of how close the camera is. What fog
// HIDES is not here and never was - that is the vision model, and it applies at every zoom.
// This is only the grey wash, and a grey wash over half a globe helps nobody read it.
uniform float fog_tint = 1.0;
uniform float fog_darkness = 0.74;
uniform float fog_saturation = 0.65;
// Positive cubic B-spline weights round raster bends without the overshoot of a
// sharpening cubic. Four hardware-linear fetches reproduce a 4x4 footprint.
// Called only near a river: most globe fragments retain the single-fetch path.
float river_contour(vec2 uv, float linear_distance) {
    if (river_curve_filter < 0.001 || abs(linear_distance) > 2.0) return linear_distance;
    vec2 size = vec2(textureSize(hydrology_map, 0));
    vec2 point = uv * size - 0.5;
    vec2 cell = floor(point);
    vec2 f = fract(point);
    vec2 inverse_f = vec2(1.0) - f;
    vec2 w0 = inverse_f * inverse_f * inverse_f / 6.0;
    vec2 w1 = (3.0 * f * f * f - 6.0 * f * f + 4.0) / 6.0;
    vec2 w2 = (-3.0 * f * f * f + 3.0 * f * f + 3.0 * f + 1.0) / 6.0;
    vec2 w3 = f * f * f / 6.0;
    vec2 first = w0 + w1;
    vec2 second = w2 + w3;
    vec2 a = (cell - 1.0 + w1 / first + 0.5) / size;
    vec2 b = (cell + 1.0 + w3 / second + 0.5) / size;
    // Longitude repeats, but neither pole may sample the opposite hemisphere.
    a.y = clamp(a.y, 0.5 / size.y, 1.0 - 0.5 / size.y);
    b.y = clamp(b.y, 0.5 / size.y, 1.0 - 0.5 / size.y);
    float north = texture(hydrology_map, a).g * first.x +
        texture(hydrology_map, vec2(b.x, a.y)).g * second.x;
    float south = texture(hydrology_map, vec2(a.x, b.y)).g * first.x +
        texture(hydrology_map, b).g * second.x;
    float curved = ((north * first.y + south * second.y) * 255.0 - 128.0) / 16.0;
    // Keep tiny authored headwaters: cubic averaging must not erase a one-cell source.
    curved = min(curved, linear_distance + 0.25);
    return mix(linear_distance, curved, river_curve_filter);
}
// Same rounding as DateLineSeam.WrappedFrequency: a whole number of cycles
// around the world, so the wave at longitude 180 is the wave at -180.
float seam_frequency(float freq, float width) {
    return sign(freq) * max(1.0, round(abs(freq) * width / 6.2831853)) * 6.2831853 / max(width, 1.0);
}
// The poles. Every meridian meets there, so anything tiled in latitude and longitude - a
// terrain tile, a wave train - is squeezed into ever thinner wedges towards them, and the
// ice caps wore a sunburst of streaks. Near a pole the tiles are laid instead on the
// stereographic plane about it: conformal, so a tile keeps its shape, and with no seam and no
// point where the columns meet. Measured in radians of the Earth's surface at the pole, so
// `per_radian` repeats match the grid's own density and the two blend without a jump in size.
// Only on the globe: the flat map is the grid, and a tile there is meant to follow it.
float polar_blend(vec2 uv) {
    if (flat_projection > 0.99) return 0.0;
    float latitude = abs(0.5 - clamp(uv.y, 0.0, 1.0)) * 180.0;
    if (latitude < polar_tiling_from) return 0.0;
    return smoothstep(polar_tiling_from, polar_tiling_from + max(polar_tiling_band, 0.1), latitude)
        * (1.0 - flat_projection);
}
vec2 polar_plane(vec2 uv, float per_radian) {
    float lon = uv.x * 6.2831853;
    float lat = (0.5 - clamp(uv.y, 0.0, 1.0)) * 3.1415927;
    float c = cos(lat);
    return vec2(c * sin(lon), c * cos(lon)) * (2.0 / (1.0 + abs(sin(lat)))) * per_radian;
}
float sea_wave_field(vec2 at, vec3 fx) {
    return (sin(dot(at, vec2(fx.x, 0.0062)) + TIME * 0.55)
          + 0.62 * sin(dot(at, vec2(fx.y, 0.0094)) + TIME * 0.41)
          + 0.38 * sin(dot(at, vec2(fx.z, -0.0230)) + TIME * 0.72)) / 2.0;
}
float sea_crest_field(vec2 at) {
    return sin(dot(at, vec2(-0.0180, 0.0094)) + TIME * 0.41
             + 1.1 * sin(dot(at, vec2(0.0330, 0.0195)) + TIME * 0.9));
}
// Mipmaps are averaged inward, so the column at u=0 never meets the column at
// u=1 and the date line reads as a second border. Near that cut, bilinear-sample
// the base level with longitude wrapped and latitude clamped. Poles stay put.
vec4 seam_lod0_political_map(vec2 uv) {
    vec2 size = vec2(textureSize(political_map, 0));
    vec2 p = vec2(fract(uv.x), clamp(uv.y, 0.0, 1.0)) * size - 0.5;
    vec2 f = fract(p);
    vec2 i = floor(p);
    float x0 = mod(i.x, size.x);
    float x1 = mod(i.x + 1.0, size.x);
    float y0 = clamp(i.y, 0.0, size.y - 1.0);
    float y1 = clamp(i.y + 1.0, 0.0, size.y - 1.0);
    vec2 texel = 1.0 / size;
    vec4 a = textureLod(political_map, (vec2(x0, y0) + 0.5) * texel, 0.0);
    vec4 b = textureLod(political_map, (vec2(x1, y0) + 0.5) * texel, 0.0);
    vec4 c = textureLod(political_map, (vec2(x0, y1) + 0.5) * texel, 0.0);
    vec4 d = textureLod(political_map, (vec2(x1, y1) + 0.5) * texel, 0.0);
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}
vec4 seam_sample_political_map(vec2 uv) {
    vec2 wrapped = vec2(fract(uv.x), clamp(uv.y, 0.0, 1.0));
    vec4 mipped = texture(political_map, wrapped);
    float width = max(float(textureSize(political_map, 0).x), 1.0);
    // Derivatives are supported by Compatibility too; textureQueryLod is not.
    // Measure the unwrapped UV so crossing the date line cannot create a huge footprint.
    vec2 texels = uv * vec2(textureSize(political_map, 0));
    float footprint = max(length(dFdx(texels)), length(dFdy(texels)));
    float span = max(footprint, 1.0) * 1.5 / width;
    float along = min(wrapped.x, 1.0 - wrapped.x);
    float at_seam = 1.0 - smoothstep(span * 0.35, max(span, 0.000001), along);
    if (at_seam <= 0.0) return mipped;
    return mix(mipped, seam_lod0_political_map(wrapped), at_seam);
}
vec4 seam_lod0_ocean_depth(vec2 uv) {
    vec2 size = vec2(textureSize(ocean_depth, 0));
    vec2 p = vec2(fract(uv.x), clamp(uv.y, 0.0, 1.0)) * size - 0.5;
    vec2 f = fract(p);
    vec2 i = floor(p);
    float x0 = mod(i.x, size.x);
    float x1 = mod(i.x + 1.0, size.x);
    float y0 = clamp(i.y, 0.0, size.y - 1.0);
    float y1 = clamp(i.y + 1.0, 0.0, size.y - 1.0);
    vec2 texel = 1.0 / size;
    vec4 a = textureLod(ocean_depth, (vec2(x0, y0) + 0.5) * texel, 0.0);
    vec4 b = textureLod(ocean_depth, (vec2(x1, y0) + 0.5) * texel, 0.0);
    vec4 c = textureLod(ocean_depth, (vec2(x0, y1) + 0.5) * texel, 0.0);
    vec4 d = textureLod(ocean_depth, (vec2(x1, y1) + 0.5) * texel, 0.0);
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}
vec4 seam_sample_ocean_depth(vec2 uv) {
    vec2 wrapped = vec2(fract(uv.x), clamp(uv.y, 0.0, 1.0));
    vec4 mipped = texture(ocean_depth, wrapped);
    float width = max(float(textureSize(ocean_depth, 0).x), 1.0);
    // Derivatives are supported by Compatibility too; textureQueryLod is not.
    // Measure the unwrapped UV so crossing the date line cannot create a huge footprint.
    vec2 texels = uv * vec2(textureSize(ocean_depth, 0));
    float footprint = max(length(dFdx(texels)), length(dFdy(texels)));
    float span = max(footprint, 1.0) * 1.5 / width;
    float along = min(wrapped.x, 1.0 - wrapped.x);
    float at_seam = 1.0 - smoothstep(span * 0.35, max(span, 0.000001), along);
    if (at_seam <= 0.0) return mipped;
    return mix(mipped, seam_lod0_ocean_depth(wrapped), at_seam);
}
vec4 seam_lod0_relief_color(vec2 uv) {
    vec2 size = vec2(textureSize(relief_color, 0));
    vec2 p = vec2(fract(uv.x), clamp(uv.y, 0.0, 1.0)) * size - 0.5;
    vec2 f = fract(p);
    vec2 i = floor(p);
    float x0 = mod(i.x, size.x);
    float x1 = mod(i.x + 1.0, size.x);
    float y0 = clamp(i.y, 0.0, size.y - 1.0);
    float y1 = clamp(i.y + 1.0, 0.0, size.y - 1.0);
    vec2 texel = 1.0 / size;
    vec4 a = textureLod(relief_color, (vec2(x0, y0) + 0.5) * texel, 0.0);
    vec4 b = textureLod(relief_color, (vec2(x1, y0) + 0.5) * texel, 0.0);
    vec4 c = textureLod(relief_color, (vec2(x0, y1) + 0.5) * texel, 0.0);
    vec4 d = textureLod(relief_color, (vec2(x1, y1) + 0.5) * texel, 0.0);
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}
vec4 seam_sample_relief_color(vec2 uv) {
    vec2 wrapped = vec2(fract(uv.x), clamp(uv.y, 0.0, 1.0));
    vec4 mipped = texture(relief_color, wrapped);
    float width = max(float(textureSize(relief_color, 0).x), 1.0);
    // Derivatives are supported by Compatibility too; textureQueryLod is not.
    // Measure the unwrapped UV so crossing the date line cannot create a huge footprint.
    vec2 texels = uv * vec2(textureSize(relief_color, 0));
    float footprint = max(length(dFdx(texels)), length(dFdy(texels)));
    float span = max(footprint, 1.0) * 1.5 / width;
    float along = min(wrapped.x, 1.0 - wrapped.x);
    float at_seam = 1.0 - smoothstep(span * 0.35, max(span, 0.000001), along);
    if (at_seam <= 0.0) return mipped;
    return mix(mipped, seam_lod0_relief_color(wrapped), at_seam);
}
vec4 seam_lod0_relief_height(vec2 uv) {
    vec2 size = vec2(textureSize(relief_height, 0));
    vec2 p = vec2(fract(uv.x), clamp(uv.y, 0.0, 1.0)) * size - 0.5;
    vec2 f = fract(p);
    vec2 i = floor(p);
    float x0 = mod(i.x, size.x);
    float x1 = mod(i.x + 1.0, size.x);
    float y0 = clamp(i.y, 0.0, size.y - 1.0);
    float y1 = clamp(i.y + 1.0, 0.0, size.y - 1.0);
    vec2 texel = 1.0 / size;
    vec4 a = textureLod(relief_height, (vec2(x0, y0) + 0.5) * texel, 0.0);
    vec4 b = textureLod(relief_height, (vec2(x1, y0) + 0.5) * texel, 0.0);
    vec4 c = textureLod(relief_height, (vec2(x0, y1) + 0.5) * texel, 0.0);
    vec4 d = textureLod(relief_height, (vec2(x1, y1) + 0.5) * texel, 0.0);
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}
vec4 seam_sample_relief_height(vec2 uv) {
    vec2 wrapped = vec2(fract(uv.x), clamp(uv.y, 0.0, 1.0));
    vec4 mipped = texture(relief_height, wrapped);
    float width = max(float(textureSize(relief_height, 0).x), 1.0);
    // Derivatives are supported by Compatibility too; textureQueryLod is not.
    // Measure the unwrapped UV so crossing the date line cannot create a huge footprint.
    vec2 texels = uv * vec2(textureSize(relief_height, 0));
    float footprint = max(length(dFdx(texels)), length(dFdy(texels)));
    float span = max(footprint, 1.0) * 1.5 / width;
    float along = min(wrapped.x, 1.0 - wrapped.x);
    float at_seam = 1.0 - smoothstep(span * 0.35, max(span, 0.000001), along);
    if (at_seam <= 0.0) return mipped;
    return mix(mipped, seam_lod0_relief_height(wrapped), at_seam);
}
void fragment() {
    float excavation = crossover_excavations_active * texture(crossover_excavations, UV).r;
    // How far this point is from the nearest frontier or shore, in cells (FrontierField). Read
    // once, here, outside every branch that depends on the pixel: the far stroke, the atlas's far
    // band and its surf all use this one sample. Keep the call spelt exactly as it is: it is the
    // one form the packed Compatibility build knows how to cut (PackedMapShader).
    float frontier_cells_all = frontier_cap;
    if (frontier_ready > 0.5) frontier_cells_all = texture(frontier_field, UV).r * frontier_cap;
    // One wander, and every boundary on the map reads through it.
    //
    // Reconstructing a boundary from the four ids around a point puts it within half a texel
    // of the truth, which is exact enough at any zoom where a texel is smaller than a pixel.
    // Close up it is not: a texel of a 5760-wide world covers ten screen pixels, half a texel
    // is five, and five pixels of error laid along a grid is a staircase. No amount of
    // sharpening helps, because there is nothing left in the raster to sharpen - the
    // information simply is not there.
    //
    // So the boundary is moved off the grid instead. The point of sampling is pushed around
    // by rather less than a texel, which is inside the error the reconstruction already has,
    // and the staircase becomes a coastline. It is the same thing the ground does where two
    // terrains meet, and the same thing a Paradox map does with a baked noise layer.
    //
    // Shared, and that is the whole reason it lives here rather than in three places:
    // country, region and province all read through this one offset, so they wander together
    // and continue to agree. Warped separately, a country border and the province border
    // that runs along it would sit a texel apart and the map would look torn.
    //
    // Faded out as the camera pulls back: once a texel is smaller than a pixel the wander
    // would only be a way of making small islands flicker.
    vec2 id_size = vec2(textureSize(nation_ids, 0));
    float id_span = max(length(fwidth(UV) * id_size), 0.0001);
    // How wide a screen pixel is, measured in coverage. A coverage field crosses from one
    // to zero over about a texel, so a pixel that spans `id_span` texels spans about that
    // much coverage - and that is the width every soft edge on this map wants to be.
    //
    // It cannot be had from `fwidth` of the coverage itself once the lookup below is warped:
    // fwidth would then be measuring how fast the noise wiggles, which is fast, and every
    // border on the map would come out several pixels wide and washed out instead of one
    // pixel wide and crisp. The camera says how big a pixel is; the noise does not get a
    // vote.
    float id_aa = max(id_span, 0.0001);
    // Four hashes apiece and two octaves each, so this pair is the most expensive thing on
    // the map that is not a texture fetch - and it is wanted three times over: by the
    // political boundary, by the ground, and by the shore. It is a pair of numbers between
    // minus a half and a half, in no units at all, so each of the three can scale it into
    // its own texels and they all end up wandering the same way. Computed three times it
    // cost forty hashes a pixel; computed once it costs sixteen.
    vec2 map_drift = vec2(0.0);
    vec2 map_uv = UV;
    if (map_smoothing > 0.5) {
        map_drift = vec2(grain(UV * id_size * 0.9), grain(UV * id_size * 0.9 + 71.3));
        map_uv = UV + map_drift * id_wander_texels * clamp(3.2 - id_span * 0.4, 0.0, 1.0) / id_size;
    }

    // Far away the authored raster is right: a screen pixel covers many map texels and what
    // the eye wants is the average, mipmapped and anisotropic. Close up the raster is exactly
    // wrong - one texel covers many pixels, so nearest sampling shows the grid it was stored
    // on. So close up the colour is rebuilt from the country id map instead, and the boundary
    // between two countries is recovered as a straight edge at whatever angle it really runs,
    // antialiased over one screen pixel. The raster is not being filtered; it is not being
    // asked.
    vec4 political_smooth = seam_sample_political_map(UV);
    // The plate is repainted from the live owners whenever a border moves, on a worker, and
    // for the moment that takes it still shows the old owner. The provinces it has not caught
    // up with are painted here in the colour it is about to give them - over the plate sample
    // alone, before it is mixed with anything, so the saturation, the land shade and every
    // line below treat moved land exactly as they treat the rest. Once the plate arrives the
    // table is empty and this does nothing.
    float province = map_id(province_ids, map_uv);
    vec4 plate_patch = plate_catch_up(province);
    political_smooth.rgb = mix(political_smooth.rgb, plate_patch.rgb, plate_patch.a);
    // Smoothed boundaries only where a texel is bigger than a pixel - further out the raster
    // is averaged by its mipmaps and has no staircase to show - and only for a player who
    // has the map smoothed at all.
    bool smooth_edges = map_smoothing > 0.5 && id_span < 8.0;
    float near_nation;
    float other_nation;
    float nation_reach;
    float nation_cover = nation_coverage(map_uv, smooth_edges, near_nation, other_nation, nation_reach);
    float nation_aa = id_aa;
    float nation_share = smoothstep(0.5 - nation_aa, 0.5 + nation_aa, nation_cover);

    // One screen pixel, measured in texels. Wanted for deciding when a line is finer than
    // the screen can carry.
    float pixel_texels = id_span;

    // A real edge, one pixel wide.
    //
    // This used to fade the fill over most of a texel - nine screen pixels close in - and let
    // a stroke on top carry the boundary. The reason was sound at the time: a hard threshold
    // laid on a reconstruction that is only accurate to a texel shows the texel pattern, and
    // a soft fade at least hid it. What it actually produced was a grey halo round every
    // coast and every border, which is how the whole map came to look slightly out of focus.
    //
    // The wander is what makes the compromise unnecessary. The boundary is no longer on the
    // grid, so there is no grid left to show, and the edge can be exactly as sharp as the
    // screen allows.
    // Smoothed, the colour is rebuilt across the reconstructed boundary. Unsmoothed, it is
    // simply the nearest texel's country - which is what nearest sampling means, and what
    // puts the texel grid back on the screen for anybody who wants it there.
    vec4 political_crisp = map_smoothing > 0.5
        ? mix(nation_paint(other_nation), nation_paint(near_nation), nation_share)
        : nation_paint(near_nation);
    vec4 political = mix(political_smooth, political_crisp, detail_mix);
    political.rgb = mix(vec3(dot(political.rgb, vec3(0.2126, 0.7152, 0.0722))), political.rgb, political_saturation);
    if (atlas_look > 0.5) {
        // The paper map is a printed atlas, a little quieter than a poster. Applied as a second
        // step over the first so the classic line above stays as it was: both keep luminance, so
        // the two compose to exactly the target saturation.
        float paper_saturation = mix(political_saturation, look_saturation_far, paper_mix);
        political.rgb = mix(vec3(dot(political.rgb, vec3(0.2126, 0.7152, 0.0722))), political.rgb,
            paper_saturation / max(political_saturation, 0.001));
        // Land no state holds is the paper itself, as in a printed atlas - not the classic slate
        // grey, which from far away sat so close to the chart sea in value and hue that unclaimed
        // land melted into the water. Shared out by the same coverage the country colours use,
        // so its edge against a country is as smooth as theirs. (65535 is MapBorders.Unowned.)
        float unowned_share = abs(near_nation - 65535.0) < 0.5 ? mix(1.0, nation_share, step(0.5, map_smoothing))
            : (abs(other_nation - 65535.0) < 0.5 ? (1.0 - nation_share) * step(0.5, map_smoothing) : 0.0);
        political.rgb = mix(political.rgb, look_unowned, unowned_share);
    }
    float rim = pow(1.0 - max(dot(normalize(NORMAL), normalize(VIEW)), 0.0), 2.0);
    // The sea has a floor. Deep water keeps the strategic navy it always had; the continental
    // slope lifts it a little, and the shelf along every coast - the North Sea, the Yellow Sea,
    // the Sunda shelf, the Grand Banks - is a clear shallow teal, so a coast reads as a coast from
    // orbit, the way it does on every Paradox map, before any border line has been drawn.
    float sea_floor = ocean_depth_ready > 0.5 ? seam_sample_ocean_depth(UV).r : 0.0;
    // A smoothstep over a smooth raster draws contour lines across an ocean that has none in
    // it: the shelf arrives as a band with an edge parallel to the texel rows. The same wander
    // the coast uses is added here, a texel or so wide, so the shelf reads as ground under
    // water rather than as a level in a depth chart.
    vec2 sea_size = vec2(textureSize(ocean_depth, 0));
    float sea_wander = grain(UV * sea_size * 0.85 + 17.0);
    float sea_polar = polar_blend(UV);
    vec2 sea_polar_at = vec2(0.0);
    if (sea_polar > 0.001) {
        sea_polar_at = polar_plane(UV, sea_size.x / 6.2831853);
        sea_wander = mix(sea_wander, grain(sea_polar_at * 0.85 + 17.0), sea_polar);
    }
    sea_floor = clamp(sea_floor + sea_wander * 0.014, 0.0, 1.0);
    float slope_rise = smoothstep(0.62, 0.955, sea_floor);
    float shelf = smoothstep(0.945, 0.992, sea_floor);
    float littoral = smoothstep(0.982, 0.999, sea_floor);
    vec3 abyss_color = ocean_color * 0.76;
    vec3 sea = mix(abyss_color, ocean_color * 1.24, slope_rise);
    sea = mix(sea, ocean_shelf_color, shelf * 0.82);
    sea = mix(sea, ocean_shelf_color * 1.22 + vec3(0.015, 0.045, 0.055), littoral * 0.55);
    if (atlas_look > 0.5) {
        // The same four depths on the same thresholds, in the atlas's own inks: warmer and
        // darker in the abyss, a clear green-grey over the shelf.
        vec3 atlas_sea = mix(look_sea_deep, look_sea_mid, slope_rise);
        atlas_sea = mix(atlas_sea, look_sea_shelf, shelf * 0.82);
        atlas_sea = mix(atlas_sea, look_sea_coast, littoral * 0.55);
        // From far away a printed chart has no gradients, only steps: one tone, a shade lighter
        // over the slope and again over the shelf. Each step is as soft as one pixel of depth.
        // The steps are about two and a half L* apart (0.72 / 0.86 / 1.0): any closer and they
        // do not register. The chart tone itself is dark - the lightest step still under a
        // third of the darkest usual country fill - so land stands above water from orbit.
        float step_width = max(fwidth(sea_floor), 0.001);
        float step_slope = smoothstep(0.80 - step_width, 0.80 + step_width, sea_floor);
        float step_shelf = smoothstep(0.968 - step_width, 0.968 + step_width, sea_floor);
        vec3 chart = look_sea_chart * (0.72 + 0.14 * (step_slope + step_shelf));
        sea = mix(atlas_sea, chart, paper_mix);
    }
    // Living water. Three directional sine waves, crossing at angles, rather than value
    // noise: noise interpolates on its own axis-aligned grid, and on a surface this large
    // the grid arrives as a tiling of diamonds with visible seams. Sines have no grid to
    // show. The directions are deliberately off-axis and incommensurate, so the combined
    // field never lines up with itself into a pattern, and the whole thing drifts slowly.
    // Faded out at strategic zoom, where the mipmaps already average the surface flat and
    // the same field would only add shimmer.
    vec2 sea_texels = vec2(fract(UV.x) * sea_size.x, clamp(UV.y, 0.0, 1.0) * sea_size.y);
    float wave_fade = clamp(1.0 - id_span, 0.0, 1.0);
    // A slow warp of the sampling point, so even the wave lines breathe rather than march.
    // The east-west term is a whole number of cycles, or the warp itself is a seam.
    float warp_x = seam_frequency(0.002, sea_size.x);
    vec2 warp = vec2(sin(TIME * 0.05 + sea_texels.y * 0.002), cos(TIME * 0.04 + sea_texels.x * warp_x));
    vec2 wave_at = sea_texels + warp * 26.0;
    vec3 wave_fx = vec3(seam_frequency(0.0105, sea_size.x), seam_frequency(-0.0180, sea_size.x),
                        seam_frequency(0.0072, sea_size.x));
    float waves = sea_wave_field(wave_at, wave_fx);
    if (sea_polar > 0.001) {
        vec2 wave_polar_at = sea_polar_at + warp * 26.0;
        waves = mix(waves, sea_wave_field(wave_polar_at, vec3(0.0105, -0.0180, 0.0072)), sea_polar);
    }
    vec3 water = sea * (0.74 + rim * 0.26);
    water *= 1.0 + waves * 0.085 * wave_fade;
    // Sun glint riding the sharpest of the three - the crests catching light, which is what
    // tells the eye this is a surface and not a fill. Kept at low zoom too: a flat map has
    // no rim to speak of, and gating it on the limb would delete it where players live.
    //
    // One crest train with its phase bent by the second wave, not the sum of the two: added
    // together they only reinforce along short diagonals, and the light landed as scattered
    // white scratches across the open sea rather than as a wave line.
    float crest = sea_crest_field(wave_at);
    if (sea_polar > 0.001) {
        vec2 wave_polar_at = sea_polar_at + warp * 26.0;
        crest = mix(crest, sea_crest_field(wave_polar_at), sea_polar);
    }
    float glint = pow(clamp(crest * 0.5 + 0.5, 0.0, 1.0), 6.0);
    // The atlas has one light on its water - the lamp's - so the crests catch its foam colour
    // rather than a cold cyan beside a warm highlight.
    water += mix(vec3(0.58, 0.75, 0.84), look_foam, step(0.5, atlas_look)) * glint * 0.18 * wave_fade;
    // Foam & coastal ink: a pale surf line where the sea meets the shore, framed by a subtle
    // cartographic coastal shadow so continents stand out crisply from the ocean.
    float shore_band = max(fwidth(political.a) * 2.2, 0.022);
    float shore_edge = smoothstep(shore_band, 0.0, abs(political.a - 0.5));
    float reef_halo = smoothstep(shore_band * 3.8, 0.0, abs(political.a - 0.5)) * (1.0 - political.a);
    vec3 reef_color = ocean_shelf_color * 1.38 + vec3(0.03, 0.09, 0.11);
    vec3 surf_color = vec3(0.84, 0.91, 0.95);
    // The one-pixel surf, and separately the dark frame on the water side of every coast below.
    // The atlas replaces only the surf (with its own, off the true distance to the shore) and
    // only where it has the field to draw it from; the frame stays at every zoom, because it is
    // what keeps a continent's outline crisp against the sea in the middle and far views.
    float surf_edge = shore_edge;
    if (atlas_look > 0.5) {
        reef_color = look_sea_coast;
        surf_color = look_foam;
        surf_edge *= 1.0 - step(0.5, frontier_ready);
    }
    water = mix(water, reef_color, reef_halo * 0.42);
    water += surf_color * surf_edge * 0.50 * wave_fade;
    water = mix(water, water * 0.58, shore_edge * 0.38 * (1.0 - political.a));
    if (atlas_look > 0.5) {
        float sea_side = 1.0 - political.a;
        // On the sea the frontier field is the distance to the shore in cells, so surf can be
        // drawn where surf is. Asked only where it can show: on water, with the field, and while
        // a cell is still bigger than a pixel. Further out a line a fraction of a cell wide is
        // finer than the screen and only shimmers.
        float foam_fade = clamp(1.0 - id_span, 0.0, 1.0);
        if (frontier_ready > 0.5 && political.a < 0.999 && foam_fade > 0.0) {
            float shore_cells = frontier_cells_all;
            float foam_noise = atlas_noise(UV, id_size.x * 0.15, flat_projection);
            // One broken line a little under a cell out. It breathes, but the breath is let go
            // well before a cell shrinks to a couple of pixels: a thin line shifting by a fraction
            // of a pixel is what reads as shimmer crawling along a coast.
            float breathe = 0.20 * sin(TIME * 0.6 + foam_noise * 6.0) * clamp(1.0 - id_span * 2.0, 0.0, 1.0);
            float foam_line = 1.0 - smoothstep(0.0, 0.30 + id_aa, abs(shore_cells - (0.9 + breathe)));
            // A soft threshold rather than a step: the break in the surf has an edge a fraction of
            // a noise cell wide instead of a hard one that would crawl as the camera moves.
            float foam = foam_line * smoothstep(0.30, 0.40, foam_noise) * foam_fade * sea_side;
            water = mix(water, look_foam, foam * look_foam_strength);
            // Waterlines - lines ruled along a coast at two, three and a half and five cells out -
            // are a nineteenth-century engraver's mark, so they belong to an era's overlay, not to
            // the base map: off (nought) in the shipped look, kept here for a skin that asks.
            if (look_waterline > 0.001) {
                float line_width = 0.25 + id_aa;
                float waterlines = (1.0 - smoothstep(0.0, line_width, abs(shore_cells - 2.0))) * 0.6
                    + (1.0 - smoothstep(0.0, line_width, abs(shore_cells - 3.5))) * 0.4
                    + (1.0 - smoothstep(0.0, line_width, abs(shore_cells - 5.0))) * 0.25;
                waterlines *= look_waterline * foam_fade * (1.0 - paper_mix) * sea_side;
                water = mix(water, look_foam, waterlines);
            }
        }
        // The lamp's broad highlight over open water, on the globe only, where it is the
        // reflection of a fixed lamp up and to the left of the camera. It brightens the water's
        // own colour rather than adding a warm constant to it: added to a dark albedo, a constant
        // turned the abyss into a neutral grey smear. The flat map has none - an orthographic
        // camera has no reflection to give, and a spot fixed to the screen read as a smudge on
        // the monitor that stayed put while the map moved under it. Gone on the paper map.
        if (PROJECTION_MATRIX[3][3] < 0.5) {
            float sheen = pow(max(dot(reflect(-VIEW, NORMAL), normalize(vec3(-0.45, 0.55, 0.70))), 0.0), 6.0);
            water *= 1.0 + sheen * look_sea_sheen * (1.0 - paper_mix) * sea_side;
        }
    }
    vec2 texel = 1.0 / vec2(textureSize(province_ids, 0));
    float region = map_id(region_ids, map_uv);
    vec2 destruction_uv = (vec2(mod(province, 256.0), floor(province / 256.0)) + 0.5) / 256.0;
    if (texture(front_membership, destruction_uv).a > 0.5) { discard; }
    float nation = map_id(nation_ids, map_uv);
    vec4 live_control = control_color(province);
    float political_light = clamp(max(max(political.r, political.g), political.b) * 1.25, 0.58, 0.94);

    // Who owns this ground now is already in `political`: close up through nation_at, which
    // reads the moved owners, and from far away through the plate, which is repainted from
    // them. There used to be a third answer laid over both here - the new owner's colour per
    // province, straight from the palette - and it is why a border a treaty had drawn never
    // looked like one the scenario had: the overlay skipped the saturation every other
    // country's paint goes through, so won land was a different tint from its own country,
    // and it was keyed to the nearest texel's province, so its edge ran along the texel grid
    // in steps beside the smooth, reconstructed line the border stroke follows.
    // Owner remains the ground; keylined controller ribbons mean occupation,
    // never annexation. One shared style for flat map and globe.
    float treaty = step(0.95, live_control.a);
    float occupied = occupation_mask(live_control);
    float leased = lease_mask(live_control);
    political.rgb = mix(political.rgb, live_control.rgb * political_light, treaty);
    political.rgb = occupation_ribbons(political.rgb, live_control.rgb, UV,
        province_detail, occupied);
    political.rgb = lease_weave(political.rgb, live_control.rgb, UV, province_detail, leased);
    // The map modes colour the country that owns this ground now (near_nation reads the moved
    // owners), not the one the raster was painted with: the diplomatic view of 2039 drew the
    // borders of 2024, Belarus annexed and all.
    vec4 strategic = map_mode_color(near_nation);
    political.rgb = mix(political.rgb, strategic.rgb * mix(0.84, 1.02, political_light),
        map_mode * strategic.a);
    vec2 resource_uv = (vec2(mod(province, 256.0), floor(province / 256.0)) + 0.5) / 256.0;
    vec4 resource = texture(resource_colors, resource_uv);
    political.rgb = mix(political.rgb, resource.rgb, resource_mode * resource.a);
    vec3 base = mix(water, political.rgb, political.a);
    if (terrain_ready > 0.5) {
        vec2 province_uv = (vec2(mod(province, 256.0), floor(province / 256.0)) + 0.5) / 256.0;
        // Where one ground meets another, reconstructed rather than stepped, and walked off
        // the grid first so the meeting is ragged instead of ruled. Two ground samples: the
        // type this pixel mostly stands on and the one with the next best claim on it, mixed
        // across a band a texel or so wide - and never narrower than a screen pixel, so a
        // coastline seen from orbit fades rather than sparkles.
        vec2 terrain_size = vec2(textureSize(terrain_surface, 0));
        vec2 terrain_lookup = UV + map_drift * terrain_warp_texels / terrain_size;

        // All paper: the ground is the sheet, and the tile rebuild (sixteen samples), the two
        // tiles and their detail are not asked at all. A branch on uniforms, so whole draws take it.
        bool paper_only = atlas_look > 0.5 && paper_mix > 0.999 && terrain_mode < 0.5;
        vec3 ground = look_paper;
        vec3 relief_tint = look_paper;
        if (!paper_only) {
            float near_slot;
            float other_slot;
            float slot_cover = terrain_coverage(terrain_surface, terrain_lookup, smooth_edges, near_slot, other_slot);
            float slot_span = max(length(fwidth(UV) * terrain_size), 0.0001);
            float slot_band = max(terrain_blend_texels * 0.25, slot_span);
            float slot_share = smoothstep(0.5 - slot_band, 0.5 + slot_band, slot_cover);

            vec2 terrain_uv = UV * vec2(terrain_repeat, terrain_repeat * 0.5);
            // Near a pole the tiles lie on the plane about it (polar_plane), blended in over a band.
            // The gradients are taken here, outside any branch, so a sample inside one stays defined.
            float terrain_polar = polar_blend(UV);
            vec2 terrain_polar_uv = vec2(0.0);
            vec2 pdx = vec2(0.0), pdy = vec2(0.0);
            if (terrain_polar > 0.001) {
                terrain_polar_uv = polar_plane(UV, terrain_repeat / 6.2831853);
                pdx = dFdx(terrain_polar_uv);
                pdy = dFdy(terrain_polar_uv);
            }
            vec2 tdx = dFdx(terrain_uv), tdy = dFdy(terrain_uv);
            // One ground sample when unsmoothed rather than two mixed: the saving is a texture
            // fetch per pixel on the largest surface the game draws, which is the half of this
            // switch that matters on a machine that needed it.
            if (map_smoothing > 0.5)
                ground = mix(texture(terrain_tiles, vec3(terrain_uv, other_slot)).rgb,
                             texture(terrain_tiles, vec3(terrain_uv, near_slot)).rgb, slot_share);
            else
                ground = texture(terrain_tiles, vec3(terrain_uv, near_slot)).rgb;
            if (terrain_polar > 0.001) {
                vec3 polar_ground;
                if (map_smoothing > 0.5)
                    polar_ground = mix(textureGrad(terrain_tiles, vec3(terrain_polar_uv, other_slot), pdx, pdy).rgb,
                                       textureGrad(terrain_tiles, vec3(terrain_polar_uv, near_slot), pdx, pdy).rgb, slot_share);
                else
                    polar_ground = textureGrad(terrain_tiles, vec3(terrain_polar_uv, near_slot), pdx, pdy).rgb;
                ground = mix(ground, polar_ground, terrain_polar);
            }
            // Close in a tile is magnified until its texels show. A second look at the same ground at
            // four times the frequency puts fine grain back under the coarse - as brightness only, so
            // the colours stay the tile's own - and only as close in as the province lines appear.
            if (terrain_detail > 0.001 && province_detail > 0.001 && flat_projection < 0.5) {
                vec2 fine_uv = mix(terrain_uv, terrain_polar_uv, step(0.5, terrain_polar));
                vec2 fdx = mix(tdx, pdx, step(0.5, terrain_polar)), fdy = mix(tdy, pdy, step(0.5, terrain_polar));
                vec3 fine = textureGrad(terrain_tiles, vec3(fine_uv * 4.0, near_slot), fdx * 4.0, fdy * 4.0).rgb;
                vec3 tile_mean = textureLod(terrain_tiles, vec3(fine_uv, near_slot), 7.0).rgb;
                float grain_shift = dot(fine - tile_mean, vec3(0.2126, 0.7152, 0.0722));
                ground *= 1.0 + terrain_detail * province_detail * grain_shift * 2.2;
            }
            relief_tint = seam_sample_relief_color(UV).rgb;
            ground = mix(ground, relief_tint, relief_color_opacity);
        }
        if (atlas_look > 0.5) {
            // From far away the ground becomes the paper the atlas is printed on. Plain paper: a
            // fibre texture under a 95 per cent wash came to a tenth of a per cent of brightness,
            // a noise per pixel spent on nothing anyone could see. The terrain and relief views
            // keep their ground at every zoom: they are about it.
            ground = mix(ground, look_paper, paper_mix * (1.0 - step(0.5, terrain_mode)));
        }
        // Political tint is an albedo blend, not transparent 3D geometry.
        // Keep surface detail while preserving recognisable controller colours.
        // How far the kind of ground may move a country's colour. It used to be allowed from
        // 0.62 to 1.1 on the globe and 0.84 to 1.03 on the flat sheet, and since kinds of ground
        // meet along the raster's own cells, a country at the middle zoom was two tones of its
        // colour laid out in squares - the map looked pixelated inside every border. The hills
        // are the hillshade's job (below), which is smooth; the tile only tints.
        float ground_light = clamp(dot(ground, vec3(0.2126, 0.7152, 0.0722)) * 1.4 + 0.50, 0.84, 1.06);
        // On the flat sheet the band is narrower still, and the colour covers more of the
        // ground: the relief shows through the hillshade below.
        ground_light = mix(ground_light, clamp(ground_light, 0.93, 1.02), flat_projection);
        float colour_cover = mix(political_opacity, min(0.93, political_opacity + 0.17), flat_projection);
        if (atlas_look > 0.5) {
            // Close in the ground shows through the country colour, as in CK3 - but through its
            // brightness, not its hue. Lowering the cover mixed the tile's own colour into the
            // fill, and where the two roughly complement each other (a purple state over green
            // forest, a blue one over ochre desert) that mixes to brown-grey mud. So the hue keeps
            // the middle zoom's cover, and close in the brightness band the ground may move the
            // paint by is widened instead: forest darker, steppe lighter, in the country's colour.
            float ground_lum = dot(ground, vec3(0.2126, 0.7152, 0.0722));
            float near_light = clamp(ground_lum * 1.4 + 0.50, 1.0 - look_ground_show_near, 1.0 + look_ground_show_near * 0.5);
            ground_light = mix(ground_light, near_light, province_detail * (1.0 - paper_mix));
            // On paper the brightness of the ground no longer reaches the fill at all: it was the
            // light paper under it that lifted every country by up to a fifth as the camera pulled
            // back, and bleached the world view.
            ground_light = mix(ground_light, 1.0, paper_mix);
            // Political colour comes in as the camera pulls back, as in HoI4: near-solid on paper.
            colour_cover = mix(colour_cover, look_cover_far, paper_mix);
        }
        vec3 colored_ground = mix(ground, political.rgb * ground_light, colour_cover);
        vec3 land_surface = terrain_mode > 1.5 ? relief_tint : (terrain_mode > 0.5 ? ground : colored_ground);
        base = mix(water, land_surface, political.a);
        vec2 ht = 1.0 / vec2(textureSize(relief_height, 0));
        // East and west a texel of longitude apart is a texel of ground at the equator and almost
        // none near a pole, where every slope then read north-south and pointed at the pole. On the
        // globe they are stepped the same distance over the ground instead.
        float relief_cos = max(cos((0.5 - clamp(UV.y, 0.0, 1.0)) * 3.1415927), 0.035);
        float ew_step = ht.x * mix(1.0 / relief_cos, 1.0, flat_projection);
        float west = seam_sample_relief_height(vec2(UV.x - ew_step, clamp(UV.y, ht.y, 1.0 - ht.y))).r;
        float east = seam_sample_relief_height(vec2(UV.x + ew_step, clamp(UV.y, ht.y, 1.0 - ht.y))).r;
        float north = seam_sample_relief_height(vec2(UV.x, clamp(UV.y - ht.y, 0.0, 1.0))).r;
        float south = seam_sample_relief_height(vec2(UV.x, clamp(UV.y + ht.y, 0.0, 1.0))).r;
        float elev = (west + east + north + south) * 0.25;
        vec2 slope = clamp(vec2(west - east, south - north) * relief_strength * 8.5, vec2(-0.85), vec2(0.85));
        float slope_mag = length(slope);
        vec3 relief_normal = normalize(vec3(slope * political.a, 1.0));
        NORMAL_MAP = normalize(vec3(slope * political.a * 0.62, 1.0)) * 0.5 + 0.5;
        NORMAL_MAP_DEPTH = 1.0;
        // West and north are -x and +y in the surface's own frame; the light stands high over
        // the north-west. Flat ground keeps its colour exactly: the shade is the difference a
        // slope makes, not a dimmer on the whole map.
        vec3 atlas_light = normalize(vec3(-0.62, 0.62, 0.46));
        float flat_lit = atlas_light.z;
        float lit = dot(relief_normal, atlas_light);
        float valley_ao = clamp(1.0 - slope_mag * 0.16 * (1.0 - smoothstep(0.45, 0.78, elev)), 0.78, 1.0);
        float hillshade = clamp((1.0 + (lit - flat_lit) * 1.48) * valley_ao, 0.52, 1.42);
        // Subtle alpine ridge highlight on high mountain chains so the Himalayas, Andes, Alps
        // and Caucasus read with physical relief even through political colour.
        float alpine_ridge = smoothstep(0.58, 0.86, elev) * smoothstep(0.12, 0.52, slope_mag) * clamp(lit - flat_lit + 0.15, 0.0, 1.0);
        if (atlas_look > 0.5) {
            // The brightness of a slope is the old grey hillshade's, exactly; on top of it only the
            // hue leans a little - towards a cool shade on the slopes turned away, towards the warm
            // lamp on those facing it, as in Imhof's relief shading. The lean is a colour with no
            // luminance of its own (the tint over its own luminance, less one), scaled by the
            // surface's luminance, so a blue country is no darker in the shade than before and a
            // red one does not clip on a lit slope: its colour survives in the mountains.
            // (MapLook.ReliefShade is the same sum, for the tests.)
            vec3 shaded = base * hillshade;
            vec3 relief_tint_colour = hillshade < 1.0 ? look_shade : look_lamp;
            vec3 relief_lean = relief_tint_colour / max(dot(relief_tint_colour, vec3(0.2126, 0.7152, 0.0722)), 0.0001) - 1.0;
            shaded = max(shaded + relief_lean * dot(shaded, vec3(0.2126, 0.7152, 0.0722)) * abs(hillshade - 1.0) * look_relief_tint, vec3(0.0));
            // On the paper map the relief stays as a soft wash.
            base = mix(base, shaded + look_lamp * 0.106 * alpine_ridge,
                political.a * relief_shading * (terrain_mode > 1.5 ? 1.0 : 0.88) * mix(1.0, look_relief_far, paper_mix));
        } else {
            base = mix(base, base * hillshade + vec3(0.09, 0.095, 0.105) * alpine_ridge,
                political.a * relief_shading * (terrain_mode > 1.5 ? 1.0 : 0.88));
        }
    }
    // Distance fields describe the drawn shore, never the simulation's river strength.
    // Rivers of low importance no longer disappear at a hard intensity threshold.
    vec3 hydrography = texture(hydrology_map, vec2(fract(UV.x), clamp(UV.y, 0.0, 1.0))).rgb;
    float lake_distance = (hydrography.r * 255.0 - 128.0) / 16.0;
    // A river is as wide as it is great: a headwater a thread, the Danube a ribbon. One inset
    // for every river drew brooks as wide as the Rhine and both as pale bands of paint.
    // The raster paints a river as a corridor one to five cells wide - seven to thirty-five
    // kilometres - and drawing the corridor drew rivers as fat sausages that swelled as the camera
    // came down. The line is drawn along the river's real course instead (river_centre_map), as
    // wide as the screen needs to see it (river_thread_px .. river_great_px) and close in never
    // wider than water of that size is (river_real_half).
    ivec2 river_map_size = textureSize(river_centre_map, 0);
    vec2 river_point = vec2(fract(UV.x), clamp(UV.y, 0.0, 1.0)) * vec2(river_map_size) - 0.5;
    ivec2 river_cell = ivec2(floor(river_point));
    float river_centre = 1.0e4;
    float river_importance = 0.0;
    vec2 river_course_sum = vec2(0.0);
    float river_weight = 0.0;
    float river_importance_sum = 0.0;
    vec2 river_fraction = fract(river_point);
    vec2 river_first_course = vec2(0.0);
    bool river_has_course = false;
    bool river_coherent = true;
    for (int ry = 0; ry < 2; ry++) {
        for (int rx = 0; rx < 2; rx++) {
            ivec2 at = river_cell + ivec2(rx, ry);
            ivec2 fetch = ivec2((at.x % river_map_size.x + river_map_size.x) % river_map_size.x,
                clamp(at.y, 0, river_map_size.y - 1));
            vec4 entry = texelFetch(river_centre_map, fetch, 0);
            if (entry.b <= 0.0) continue;
            vec2 course = vec2(at) + (entry.rg * 255.0 - 128.0) / 16.0;
            if (!river_has_course) { river_first_course = course; river_has_course = true; }
            else if (length(course - river_first_course) > 1.6) river_coherent = false;
            float weight = (rx == 0 ? 1.0 - river_fraction.x : river_fraction.x) *
                (ry == 0 ? 1.0 - river_fraction.y : river_fraction.y);
            river_course_sum += course * weight;
            river_importance_sum += entry.b * weight;
            river_weight += weight;
            float reach = length(river_point - course);
            if (reach < river_centre) { river_centre = reach; river_importance = entry.b; }
        }
    }
    // Interpolate projected POINTS, not scalar distances or extended tangents. A
    // straight course is reconstructed exactly; bends become one continuous curve,
    // not a union of tangent spikes or pairwise chords (which draw little ladders).
    // Large jumps mean unrelated channels: retain bounded point caps there instead
    // of inventing a third river between them. Same four fetches, no new texture.
    if (river_coherent && river_weight > 0.0001) {
        river_centre = length(river_point - river_course_sum / river_weight);
        river_importance = river_importance_sum / river_weight;
    }
    vec2 river_line = vec2(river_centre, river_importance);
    float river_size = smoothstep(0.55, 0.98, river_importance);
    vec2 river_texels = UV * vec2(river_map_size);
    float river_texel_px = max(max(length(dFdx(river_texels)), length(dFdy(river_texels))), 0.0001);
    float river_half = max(mix(0.25, 1.0, river_size) * river_real_half,
        mix(river_thread_px, river_great_px, river_size) * river_texel_px);
    float river_distance = river_line.g > 0.0 ? river_centre - river_half : 1.0e4;
    float lake_edge = max(fwidth(lake_distance) * 0.65, 0.08);
    float river_edge = max(river_texel_px * 0.75, 0.005);
    float lake_water = 1.0 - smoothstep(-lake_edge, lake_edge, lake_distance);
    float river_water = 1.0 - smoothstep(-river_edge, river_edge, river_distance);
    float prominent_river = smoothstep(0.78, 0.88, river_line.g);
    float inland_coverage = max(lake_water, river_water);
    float lake_depth = smoothstep(0.0, water_depth_reach, -lake_distance);
    float lake_bank = (1.0 - smoothstep(0.0, water_shore_width + lake_edge, abs(lake_distance))) * lake_water;
    vec2 water_uv = vec2(UV.x * 2.0, UV.y) * water_repeat;
    // A static atlas is the default. Optional subtle animation affects material only,
    // never shoreline, ownership, picking or the paused campaign clock.
    vec2 water_motion = vec2(TIME * water_animation_speed, 0.0);
    float water_detail = max(detail_mix, province_detail) * water_texture_strength;
    vec3 inland_water = mix(vec3(0.028, 0.145, 0.185), vec3(0.014, 0.080, 0.125), lake_depth);
    // River water is water: a clear blue that reads against every political colour - thin, but
    // never the same grey as a border line. The near-black
    // teal it was drew the Han and the Dnieper exactly like the grey region lines beside them, so
    // a player could not tell a river from a border. Great channels run lighter, and carry a
    // brighter current down their middle.
    vec3 river_color = mix(vec3(0.045, 0.190, 0.330), vec3(0.065, 0.260, 0.430), prominent_river);
    float river_current = (1.0 - smoothstep(0.0, river_half * 0.45 + river_edge, river_centre)) * prominent_river;
    river_color = mix(river_color, vec3(0.200, 0.470, 0.620), river_current * 0.55);
    float river_bank = (1.0 - smoothstep(0.0, river_edge * 2.5, abs(river_distance))) * (1.0 - river_water);
    if (inland_coverage > 0.001 && water_detail > 0.001) {
        vec3 lake_tile = texture(lake_surface, water_uv + water_motion * 0.30).rgb;
        vec3 river_tile = texture(river_surface, water_uv + water_motion).rgb;
        // Neutral, bounded luminance variation preserves cartographic water colour even
        // when a mod supplies a brighter tile. Mips suppress distant crawling noise.
        float lake_grain = clamp(dot(lake_tile, vec3(0.2126, 0.7152, 0.0722)) * 12.0 - 0.55, -0.5, 0.7);
        float river_grain = clamp(dot(river_tile, vec3(0.2126, 0.7152, 0.0722)) * 12.0 - 0.55, -0.5, 0.7);
        inland_water *= 1.0 + lake_grain * water_detail;
        river_color *= 1.0 + river_grain * water_detail;
        float ripple = sin(water_uv.x * 9.0 + water_uv.y * 11.0 +
            sin(water_uv.y * 2.7) * 1.7 - TIME * water_animation_speed * 24.0);
        float ripple_fade = 1.0 - smoothstep(0.12, 0.45, max(length(dFdx(water_uv)), length(dFdy(water_uv))));
        inland_water *= 1.0 + ripple * water_ripple_strength * water_detail * ripple_fade;
    }
    base = mix(base, inland_water, lake_water);
    base = mix(base, vec3(0.075, 0.175, 0.185), lake_bank * 0.20);
    float river_opacity = mix(river_far_opacity, river_near_opacity, max(detail_mix, province_detail));
    // A line a pixel or two wide has no width to spare for transparency: drawn at the far opacity
    // the rivers of a whole continent faded out of the middle zoom.
    river_opacity = max(river_opacity, 0.85);
    river_opacity = mix(river_opacity, max(river_opacity, 0.92), prominent_river);
    base = mix(base, river_color, river_water * river_opacity * (1.0 - lake_water));
    // A thin green-brown bank, so the water sits in a valley rather than lying on the land like
    // paint - and not a black outline, which is what a border is drawn with.
    base = mix(base, base * vec3(0.78, 0.86, 0.74), river_bank * river_opacity * 0.50 * (1.0 - lake_water));
    // Land relief must not turn a lake into a mountain ridge.
    NORMAL_MAP = mix(NORMAL_MAP, vec3(0.5, 0.5, 1.0), inland_coverage);
    // The region line, the last one still drawn by asking four neighbours whether they
    // differ - which is one texel wide, on or off, and a staircase. Off the same coverage
    // field as the province and country lines.
    float near_region;
    float other_region_id;
    float region_reach;
    float region_cover = boundary_coverage(region_ids, map_uv, smooth_edges, near_region, other_region_id, region_reach);
    float region_aa = id_aa;
    float region_edge = 1.0 - smoothstep(0.0, border_pixels, region_reach / region_aa);
    region_edge *= step(0.5, abs(near_region - other_region_id));
    // A province line drawn by asking four neighbours whether they differ is one texel wide
    // and either on or off, which is a staircase wearing a border's clothes. The same
    // coverage field gives the distance to the boundary in screen pixels, so the line can be
    // a stroke of constant width with soft shoulders - crisp at any zoom, and gone rather
    // than aliased when a pixel covers more of the map than a line could describe.
    float near_province;
    float other_province_id;
    float province_reach;
    float province_cover = boundary_coverage(province_ids, map_uv, smooth_edges, near_province, other_province_id, province_reach);
    float province_aa = id_aa;
    float province_distance = province_reach / province_aa;
    float province_width = mix(border_pixels, province_border_pixels, province_detail);
    float province_edge = 1.0 - smoothstep(province_width * 0.3, province_width, province_distance);
    province_edge *= step(0.5, abs(near_province - other_province_id));
    float province_halo = (1.0 - smoothstep(province_width, province_width + 1.0, province_distance)) *
        step(0.5, abs(near_province - other_province_id));
    // The selection tint and its gold edge read their share from the coverage fields rather
    // than a hard per-texel id test. The mask drew the outline as a staircase on the texel
    // grid at every zoom a texel was still visible - the ugly jagged border on a picked
    // region. A share of the pixel fades the boundary the way every other line on this map
    // fades.
    float region_selected = (abs(near_region - selected_region_id) < 0.5 ? region_cover
        : abs(other_region_id - selected_region_id) < 0.5 ? 1.0 - region_cover : 0.0)
        * selection_active * political.a;
    // The whole live region, by its provinces, and its outline where membership changes - not the
    // authored region the raster still remembers.
    float near_member = selected_member(near_province);
    float other_member = selected_member(other_province_id);
    float live_selected = ((near_member > 0.5 ? province_cover : 0.0) + (other_member > 0.5 ? 1.0 - province_cover : 0.0))
        * selection_active * political.a;
    float live_edge = (1.0 - smoothstep(0.0, border_pixels, province_reach / province_aa))
        * step(0.5, abs(near_member - other_member));
    region_selected = mix(region_selected, live_selected, step(0.5, selected_region_live));
    region_edge = mix(region_edge, live_edge, step(0.5, selected_region_live));
    float province_selected = (abs(near_province - selected_province_id) < 0.5 ? province_cover
        : abs(other_province_id - selected_province_id) < 0.5 ? 1.0 - province_cover : 0.0)
        * selection_active * political.a;

    // The country line, off the same coverage field as the province and region lines rather
    // than off a distance field.
    //
    // A distance field gives a smoother zero line, which is why it was here - but its value
    // is a distance, and a distance read through a warped lookup is no longer a distance. The
    // wander changes the field's gradient as well as its position, so wherever the wander
    // happened to run along the border the stroke swelled into a blotch. Coverage does not
    // have that problem: it is recomputed from the ids at the warped point, so it is a
    // correct reconstruction of a boundary that has been moved, rather than a distorted
    // reading of one that has not.
    //
    // Wider than a province line, and it fades out rather than aliasing once a screen pixel
    // covers more map than a line could carry.
    // Solid to a pixel and a bit, then two more pixels of shoulder. Written as a distance
    // in screen pixels rather than in coverage, so the line is the same weight at every zoom
    // - which is what makes it read as a drawn border and not as an artefact of the data.
    float nation_edge_pixels = nation_reach / nation_aa;
    float has_nation_border = step(0.5, abs(near_nation - other_nation)) * step(0.5, near_nation);
    // From the middle zoom out, where a cell is a pixel or two and the rebuild above is asked at
    // one point a pixel, the frontier is read from the filtered distance field instead: an even
    // stroke where the rebuild gives the raster's staircase. Both say the same thing where they
    // meet - the edge between two holders - so the change is a blend over a short span of zoom,
    // and everything drawn off these two numbers (the stroke, the cushion, the rim, the outline
    // of a picked country) changes with it.
    // Only where the rebuild would alias: a cell under about two pixels. Closer in, where a cell
    // is two pixels or more, the rebuild is sharper than any filtered field.
    float frontier_weight = frontier_ready * smoothstep(0.62, 0.85, id_span);
    if (frontier_weight > 0.001) {
        float frontier_cells = frontier_cells_all;
        float frontier_pixels = max(frontier_cells - frontier_offset, 0.0) / id_aa;
        float frontier_near = 1.0 - smoothstep(frontier_reach - 0.3, frontier_reach + 0.3, frontier_cells);
        nation_edge_pixels = mix(nation_edge_pixels, frontier_pixels, frontier_weight);
        has_nation_border = mix(has_nation_border, frontier_near, frontier_weight);
    }
    float country_edge = 1.0 - smoothstep(border_pixels * 0.85, border_pixels * 2.05, nation_edge_pixels);
    // Off the field the line can be kept to the world view: it is filtered, so it thins rather
    // than breaking into dots. Off the rebuild it has to fade once a pixel holds more than a
    // cell and a half, or it would.
    float far_line = mix(clamp(2.4 - pixel_texels, 0.0, 1.0),
                         clamp(1.15 - (pixel_texels - 1.5) * 0.09, 0.5, 1.0), frontier_weight);
    country_edge *= has_nation_border * far_line;
    // Where one side is the sea the line is a coast, not a frontier, and the atlas inks it lighter
    // and bluer, so the eye can tell where a state ends from where the land does. The two inks
    // split one stroke; nothing is drawn twice.
    //
    // Close in, whether one side is the sea is read off the block of ids the stroke itself is rebuilt
    // from. From the middle zoom out the stroke comes from the filtered frontier field, which
    // reaches further than that block, and a block read at one point a pixel aliases: the land
    // half of a coast stroke came out two-toned, and the ink crawled as the camera moved. There
    // the question is put to something filtered instead - the far plate's land mask, averaged
    // over about four cells by its mipmaps: under one wherever the sea is within about two cells.
    // A frontier running down to a coast takes the coast's ink for its last few cells, which is
    // where the two lines meet anyway.
    float coast_edge = 0.0;
    float coast_far = 0.0;
    if (atlas_look > 0.5) {
        float coast_side = max(1.0 - step(0.5, near_nation), 1.0 - step(0.5, other_nation));
        if (frontier_weight > 0.001) {
            vec2 plate_size = vec2(textureSize(political_map, 0));
            float coast_lod = log2(max(4.0 * plate_size.x / id_size.x, 1.0));
            float land_around = textureLod(political_map, vec2(fract(UV.x), clamp(UV.y, 0.0, 1.0)), coast_lod).a;
            coast_far = 1.0 - smoothstep(0.92, 0.995, land_around);
            coast_side = mix(coast_side, coast_far, frontier_weight);
        }
        coast_edge = country_edge * coast_side;
        country_edge -= coast_edge;
    }
    // The highest rank wins. Where the province boundary under this pixel IS the country
    // boundary - the same edge, reconstructed twice from the same block of ids, so the two
    // reaches agree - only the country border is drawn: no province line and pale shoulder
    // under it, and no occupation contour over it. A province line that merely runs up to a
    // frontier is another edge with another reach, and is drawn to the junction as before.
    float on_country_border = has_nation_border * step(0.5, abs(near_province - other_province_id)) *
        (1.0 - smoothstep(0.10, 0.30, abs(nation_reach - province_reach)));
    float province_line = province_edge * (1.0 - on_country_border);
    float province_shoulder = province_halo * (1.0 - on_country_border);
    // Paradox-style inner frontier cushion: deepens and enriches each country's colour along the
    // inside of its international border and coastline, giving territories physical volume.
    float country_cushion = (1.0 - smoothstep(border_pixels * 1.0, border_pixels * 6.8, nation_edge_pixels)) *
        has_nation_border * clamp(2.8 - pixel_texels * 0.85, 0.0, 1.0) * (1.0 - step(0.5, terrain_mode));
    float country_inner_rim = smoothstep(border_pixels * 0.75, border_pixels * 1.35, nation_edge_pixels) *
        (1.0 - smoothstep(border_pixels * 1.35, border_pixels * 2.35, nation_edge_pixels)) *
        has_nation_border * clamp(2.2 - pixel_texels, 0.0, 1.0) * (1.0 - step(0.5, terrain_mode));
    // The region line is a shade rather than a stroke, and it gives way to a country border.
    float region_cushion = (1.0 - smoothstep(region_border_pixels * 0.1786, region_border_pixels, region_reach / region_aa)) *
        step(0.5, abs(near_region - other_region_id)) * (1.0 - has_nation_border) * (1.0 - step(0.5, terrain_mode));
    vec3 front = front_data(province);
    // Use the same reconstructed boundary as the province contour, not a stripe
    // one/two source texels wide. Source texels become giant steps at close zoom.
    vec3 near_front = front_data(near_province);
    vec3 other_front = front_data(other_province_id);
    float front_pair = step(0.002, near_front.r) * step(0.002, other_front.r) *
        (1.0 - step(0.002, abs(near_front.r - other_front.r))) *
        step(0.20, abs(near_front.g - other_front.g)) *
        step(0.5, abs(near_province - other_province_id));
    float front_edge = (1.0 - smoothstep(front_pixels * 0.45, front_pixels + 0.55, province_distance)) * front_pair;
    float front_outer = (1.0 - smoothstep(front_pixels + 0.55, front_pixels + 1.45, province_distance)) * front_pair;
    // The terrain/relief views contain no political front shading.
    front_edge *= 1.0 - step(0.5, terrain_mode);
    front_outer *= 1.0 - step(0.5, terrain_mode);

    // Apply the inner border cushion and subtle regional border shading before crisp strokes.
    // Two-stage Paradox-grade frontier gradient: a wide velvet vignette (0..8.5px) that gives
    // territories physical volume, plus a rich saturated inner rim (0.9..2.6px) right inside the
    // dark cartographic ink line.
    float country_wide_glow = (1.0 - smoothstep(border_pixels * 1.2, border_pixels * 8.5, nation_edge_pixels)) *
        has_nation_border * clamp(2.9 - pixel_texels * 0.80, 0.0, 1.0) * (1.0 - step(0.5, terrain_mode));
    vec3 saturated_nation = clamp(mix(vec3(dot(political.rgb, vec3(0.2126, 0.7152, 0.0722))), political.rgb, 1.32) * 0.84, vec3(0.08), vec3(0.95));
    vec3 cushioned = mix(base * 0.74, base * 0.52 + saturated_nation * 0.38, 0.55);
    base = mix(base, cushioned, country_cushion * max(detail_mix, 0.48) * political.a * 0.58);
    base = mix(base, mix(base, saturated_nation, 0.38), country_wide_glow * max(detail_mix, 0.38) * political.a * 0.34);
    base = mix(base, region_border_color, region_cushion * detail_mix * political.a * region_border_opacity);
    base = mix(base, clamp(base * 1.18 + vec3(0.055, 0.048, 0.032), vec3(0.0), vec3(1.0)),
        country_inner_rim * detail_mix * political.a * 0.40);
    if (atlas_look > 0.5) {
        // The far band (Imperator): from the middle zoom out, where the cushion above has gone
        // because it is measured in pixels, the colour deepens towards every frontier and coast
        // off the frontier field instead. The border keeps its volume all the way out. Its width
        // is a few screen pixels, capped by what the field holds: measured in cells it covered a
        // growing share of a small state as the camera pulled back, until Belgium, Switzerland or
        // an island nation read darker and more saturated than its real colour. Half as deep
        // along a coast, which has its own ink already. Not in Compatibility, which has no field
        // (frontier_ready stays nought there).
        float band_end = min(frontier_cap * 0.85, frontier_offset + look_far_band_pixels * id_aa);
        float raw_far_band = (1.0 - smoothstep(frontier_offset, band_end, frontier_cells_all)) * frontier_ready
            * clamp((id_span - 0.8) / 1.2, 0.0, 1.0) * political.a * (1.0 - step(0.5, terrain_mode))
            * (1.0 - 0.5 * coast_far);
        float far_band = pow(max(raw_far_band, 0.0), 1.35);
        base = mix(base, saturated_nation * 0.80, far_band * look_far_band);
    }

    // Zoom-gated, screen-space strokes: readable through relief at tactical distance,
    // absent on the world map. A restrained light shoulder also works on dark terrain.
    base = mix(base, province_halo_color, province_shoulder * province_detail * political.a * province_border_halo);
    base = mix(base, province_border_color, province_line * province_detail * political.a * province_border_opacity);
    // The country border, drawn rather than baked. It replaces the one-texel darkening the
    // political raster used to carry, which was the most visible pixel grid on the map.
    // Full weight on the world map too: at 0.35 the frontier between two states was a hairline
    // under the province grid, and the strategic view lost the one line it is for.
    // One ink for every frontier: has_nation_border asks only whether the holder changes here.
    base = mix(base, country_border_color,
        country_edge * max(detail_mix, country_border_far_weight) * political.a * country_border_opacity);
    if (atlas_look > 0.5)
        base = mix(base, look_coast_ink,
            coast_edge * max(detail_mix, country_border_far_weight) * political.a * country_border_opacity);
    // The country the player has asked about, drawn as one. Off the same coverage field as
    // the border above - the same stroke at a different weight and colour, rather than a
    // second idea of where a country ends - with a faint wash inside it so the outline reads
    // as a country rather than as a region that happens to be lit.
    float highlighted = step(0.0, highlighted_nation) *
                        (abs(near_nation - highlighted_nation) < 0.5 ? 1.0 : 0.0) * political.a;
    // The atlas draws the gold a little heavier (look_selection_width) and nothing more: a glow
    // round it would need to know which side of a line is the picked country further out than
    // the block of ids around the line can say, so it came out clipped to a texel and unseen -
    // and a stroke suits a map of ink better than a lit edge.
    float selection_width = mix(1.0, look_selection_width, step(0.5, atlas_look));
    float highlight_edge = 1.0 - smoothstep(border_pixels * 0.5 * selection_width, border_pixels * 3.2 * selection_width,
        nation_edge_pixels);
    highlight_edge *= step(0.5, abs(near_nation - other_nation)) * highlighted;
    base = mix(base, vec3(0.91, 0.79, 0.42), highlighted * 0.085);
    base = mix(base, vec3(1.00, 0.87, 0.48), highlight_edge * 0.88);
    // A line the player drew. Blue holds it, amber takes it, grey falls back to it.
    float plan = round(front.b * 4.0);
    vec3 plan_color = plan < 1.5 ? vec3(0.24, 0.56, 0.96)
        : (plan < 2.5 ? vec3(0.96, 0.52, 0.16) : vec3(0.70, 0.73, 0.78));
    float plan_on = step(0.5, plan) * max(province_detail, detail_mix * 0.70);
    if (plan < 1.5) plan_on = 0.0;
    float border_plan = 0.0;
    if (plan > 0.5 && plan < 1.5) border_plan = max(max(planned_border(province, UV + vec2(texel.x, 0.0)),
        planned_border(province, UV - vec2(texel.x, 0.0))),
        max(planned_border(province, UV + vec2(0.0, texel.y)), planned_border(province, UV - vec2(0.0, texel.y))));
    base = mix(base, plan_color, plan_on * 0.40);
    // Reuse the reconstructed province boundary; no neighbour raster pass.
    vec4 near_control = control_color(near_province);
    vec4 other_control = control_color(other_province_id);
    float near_occupied = occupation_mask(near_control);
    float other_occupied = occupation_mask(other_control);
    float control_change = 0.0;
    if (near_occupied * other_occupied > 0.5)
        control_change = step(0.5, abs(occupation_controller(near_province) - occupation_controller(other_province_id)));
    float occupation_pair = max(abs(near_occupied - other_occupied), control_change * max(near_occupied, other_occupied));
    // The pale contour marks where held ground ends INSIDE one owner's land. Where the edge
    // of an occupation is also a frontier - the occupier's own border, or a third country's -
    // the frontier is still the frontier and is drawn as one: an occupation is not an
    // annexation, and a country does not lose its outline by being overrun.
    float occupation_edge = occupation_pair * step(0.5, abs(near_province - other_province_id)) * political.a *
        (1.0 - step(0.5, terrain_mode)) * (1.0 - resource_mode) * (1.0 - map_mode) * (1.0 - on_country_border);
    float occupation_key = 1.0 - smoothstep(1.1, 2.5, province_distance);
    float occupation_stroke = 1.0 - smoothstep(0.35, 1.1, province_distance);
    base = mix(base, occupation_key_color, occupation_edge * occupation_key * 0.90);
    base = mix(base, occupation_line_color, occupation_edge * occupation_stroke * 0.94);
    float lease_edge = abs(lease_mask(near_control) - lease_mask(other_control)) *
        step(0.5, abs(near_province - other_province_id)) * political.a *
        (1.0 - step(0.5, terrain_mode)) * (1.0 - resource_mode) * (1.0 - map_mode) * (1.0 - on_country_border);
    float lease_dash = step(0.45, fract((UV.x * 2.0 - UV.y) * mix(90.0, 520.0, province_detail)));
    vec3 tenant = max(near_control.a, other_control.a) > 0.0
        ? (lease_mask(near_control) > 0.5 ? near_control.rgb : other_control.rgb) : vec3(0.9);
    base = mix(base, tenant * 1.08, lease_edge * occupation_stroke * lease_dash * 0.95);
    // A live hostile front takes precedence over the pale occupation contour.
    // The same red stroke remains on unoccupied enemy borders; rear occupation
    // boundaries stay pale rather than pretending to be an active front.
    base = mix(base, vec3(0.24, 0.03, 0.025), front_outer * 0.78);
    base = mix(base, vec3(1.0, 0.26, 0.14), front_edge * 0.98);
    // One narrow cartographic stroke on the shared boundary, never a filled province.
    base = mix(base, vec3(0.30, 0.72, 0.96), border_plan * 0.95);
    base = mix(base, vec3(0.95, 0.76, 0.25), region_selected * 0.12);
    base = mix(base, vec3(1.0, 0.84, 0.30), province_selected * province_detail * 0.18);
    float selection_edge = max(region_edge * region_selected, province_edge * province_selected * province_detail);
    // Excavation exposes earth in its exact footprint, never the space behind the globe.
    // The guest renders its real native hole separately; its atlas bake disables this mask.
    base = mix(base, vec3(0.20, 0.13, 0.09), clamp(excavation, 0.0, 1.0));
    vec2 fog_lookup = (vec2(mod(province, 256.0), floor(province / 256.0)) + 0.5) / 256.0;
    float known = texture(fog_visibility, fog_lookup).r;
    float fog = step(0.0, fog_viewer) * (1.0 - step(0.5, known)) * political.a * fog_tint * (1.0 - resource_mode);
    base = mix(base, mix(vec3(dot(base, vec3(0.2126, 0.7152, 0.0722))), base, fog_saturation) * fog_darkness, fog);
    float front_pulse = 0.85 + 0.15 * sin(TIME * 3.0);
    float front_march = 0.78 + 0.22 * sin((UV.x * 420.0 + UV.y * 280.0) - TIME * 4.2);
    ALBEDO = mix(base, vec3(1.0, 0.86, 0.34), selection_edge * 0.94);
    EMISSION = mix(water * 0.025, vec3(0.32, 0.21, 0.035), selection_edge * 0.56) +
        vec3(0.18, 0.14, 0.04) * highlight_edge * 0.38 +
        plan_color * plan_on * 0.16 +
        vec3(0.095, 0.024, 0.006) * front_edge * front_pulse * front_march +
        inland_water * (lake_water * 0.035 + river_water * 0.025);
    // Sea glint as a soft sheen rather than a white disc: the open Pacific read as fog at 0.48/0.36.
    ROUGHNESS = mix(0.56, mix(0.84, 0.40, inland_coverage), political.a);
    METALLIC = 0.0;
    SPECULAR = mix(0.26, mix(0.12, 0.40, inland_coverage), political.a);
    // A flat map is a map: under the orthographic camera the sun's glint on the water landed as
    // a white haze across whole seas (the Sea of Japan, the open Pacific). The globe keeps it.
    SPECULAR *= 1.0 - step(0.5, PROJECTION_MATRIX[3][3]);
}
