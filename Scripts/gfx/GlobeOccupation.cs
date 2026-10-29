namespace DealOrForce;

public sealed partial class GlobePresentation
{
    // Pure shader arithmetic: no extra texture, mesh, per-frame CPU work or animation.
    // Dark key + pale shoulder distinguish occupation even with identical national colours.
    private const string OccupationShaderFunctions = @"
float occupation_mask(vec4 control) {
    return step(0.10, control.a) * (1.0 - step(0.75, control.a));
}
vec3 occupation_ribbons(vec3 owner, vec3 controller, vec2 at, float detail, float active) {
    float phase = (at.x * 2.0 + at.y) * mix(56.0, 360.0, detail);
    float footprint = max(fwidth(phase), 0.0001);
    float distance_to_band = abs(fract(phase) - 0.5);
    // At the sampling limit the control boundary carries the information.
    // Never replace unresolved ribbons with an annexation-like wash.
    float visible = 1.0 - smoothstep(0.18, 0.42, footprint);
    float aa = min(footprint * 0.6, 0.055);
    float shoulder = 1.0 - smoothstep(0.17 - aa, 0.17 + aa, distance_to_band);
    float key = 1.0 - smoothstep(0.135 - aa, 0.135 + aa, distance_to_band);
    float ribbon = 1.0 - smoothstep(0.085 - aa, 0.085 + aa, distance_to_band);
    vec3 inked = mix(owner, vec3(0.89, 0.86, 0.73), shoulder * 0.82);
    inked = mix(inked, vec3(0.055, 0.075, 0.085), key * 0.86);
    inked = mix(inked, controller, ribbon * 0.94);
    return mix(owner, inked, active * visible);
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
";
}
