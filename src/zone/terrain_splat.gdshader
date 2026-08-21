// Client-owned replacement for the converter's terrain_splat.gdshader.
// Swapped onto converted terrain materials at load (ZoneLoader), keeping the
// converter's uniform names so the authored .tres parameters keep binding.
//
// Lighting model (retail-faithful two-term combine): the terrain lightmap is
// NOT a color texture. R is baked ambient/sky visibility and G is baked direct
// light visibility (sun outdoors, torch/point sources in interiors); B carries
// no data in 1.1 content. The engine colors those scalars with the zone's
// authored ZoneLights:
//
//     light   = ambient_light * R + direct_light * G
//     surface = albedo * light
//
// ZoneLoader.ApplyZoneLighting binds ambient_light = AmbientColor *
// AmbientFactor and direct_light = the zone's authored direct source
// (PointLightColor when authored, else DiffuseColor). Terrain is entirely
// baked-lit like retail: ALBEDO stays zero and EMISSION carries the combined
// product, so the scene sun and ambient no longer re-light it while depth fog
// still applies.
shader_type spatial;
render_mode cull_back;

uniform sampler2D lightmap_tex : filter_linear, repeat_disable;
uniform sampler2D splat0 : filter_linear, repeat_disable;
uniform sampler2D splat1 : filter_linear, repeat_disable;
uniform sampler2D splat2 : filter_linear, repeat_disable;
uniform sampler2D splat3 : filter_linear, repeat_disable;
uniform int splat_count = 0;
uniform sampler2D layer_atlas : source_color, filter_linear, repeat_disable;
uniform vec2 layer_grid = vec2(1.0);
uniform sampler2D pass_layers0 : filter_nearest, repeat_disable;
uniform sampler2D pass_layers1 : filter_nearest, repeat_disable;
uniform sampler2D pass_layers2 : filter_nearest, repeat_disable;
uniform sampler2D pass_params0 : filter_nearest, repeat_disable;
uniform sampler2D pass_params1 : filter_nearest, repeat_disable;
uniform sampler2D pass_params2 : filter_nearest, repeat_disable;
uniform float tile_scale = 8.0;
// Zone light colors for the two-term combine; the defaults reproduce a
// neutral mid-grey bake for contexts that never call ApplyZoneLighting.
uniform vec3 ambient_light = vec3(0.5);
uniform vec3 direct_light = vec3(0.5);

varying vec3 terrain_world;

void vertex() {
    terrain_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

vec4 sample_splat(int index, vec2 uv) {
    if (index == 0) return texture(splat0, uv);
    if (index == 1) return texture(splat1, uv);
    if (index == 2) return texture(splat2, uv);
    if (index == 3) return texture(splat3, uv);
    return vec4(0.0);
}

int byte_value(float value) {
    return int(floor(value * 255.0 + 0.5));
}

vec3 sample_layer(int index, vec2 tiled_uv) {
    float layer = float(index);
    vec2 cell = vec2(mod(layer, layer_grid.x), floor(layer / layer_grid.x));
    vec2 atlas_size = vec2(textureSize(layer_atlas, 0));
    vec2 cell_size = atlas_size / layer_grid;
    vec2 atlas_pixel = cell * cell_size + vec2(0.5) + fract(tiled_uv) * (cell_size - vec2(1.0));
    vec2 atlas_uv = atlas_pixel / atlas_size;
    return texture(layer_atlas, atlas_uv).rgb;
}

vec3 material_pass(vec4 layers, vec4 params, vec2 patch_local, vec2 tiled_uv) {
    int packed_material = byte_value(params.b);
    int splat_index = packed_material % 128;
    vec2 patch_cell = floor(params.gr * 255.0 + vec2(0.5));
    vec2 atlas_uv = (patch_cell * 64.0 + vec2(4.0) + patch_local.yx * 56.0) / 2048.0;
    vec3 weights = sample_splat(splat_index, atlas_uv).rgb;
    vec3 color = vec3(0.0);
    int layer0 = byte_value(layers.r);
    int layer1 = byte_value(layers.g);
    int layer2 = byte_value(layers.b);
    if (layer0 < 255) color += sample_layer(layer0, tiled_uv) * weights.r;
    if (layer1 < 255) color += sample_layer(layer1, tiled_uv) * weights.g;
    if (layer2 < 255) color += sample_layer(layer2, tiled_uv) * weights.b;
    return color;
}

bool pass_is_additive(vec4 params) {
    return byte_value(params.b) >= 128;
}

void fragment() {
    vec2 terrain_uv = clamp(UV, vec2(0.0), vec2(0.999999));
    vec2 patch_position = terrain_uv * 32.0;
    vec2 lookup_uv = (floor(patch_position) + vec2(0.5)) / 32.0;
    vec2 patch_local = fract(patch_position);
    vec2 tiled_uv = vec2(terrain_world.x, -terrain_world.z) / tile_scale;
    vec3 color = vec3(0.5);
    bool has_color = false;

    vec4 layers0 = texture(pass_layers0, lookup_uv);
    vec4 params0 = texture(pass_params0, lookup_uv);
    if (splat_count > 0 && params0.a > 0.5) {
        color = material_pass(layers0, params0, patch_local, tiled_uv);
        has_color = true;
    }
    vec4 layers1 = texture(pass_layers1, lookup_uv);
    vec4 params1 = texture(pass_params1, lookup_uv);
    if (splat_count > 0 && params1.a > 0.5) {
        vec3 next_color = material_pass(layers1, params1, patch_local, tiled_uv);
        if (!has_color || !pass_is_additive(params1)) color = next_color;
        else color += next_color;
        has_color = true;
    }
    vec4 layers2 = texture(pass_layers2, lookup_uv);
    vec4 params2 = texture(pass_params2, lookup_uv);
    if (splat_count > 0 && params2.a > 0.5) {
        vec3 next_color = material_pass(layers2, params2, patch_local, tiled_uv);
        if (!has_color || !pass_is_additive(params2)) color = next_color;
        else color += next_color;
    }

    vec4 lightmap = texture(lightmap_tex, vec2(terrain_uv.x, 1.0 - terrain_uv.y));
    // Modulate-2x, like the source engine: authored light colors sit at half
    // intensity so the bake can over-brighten the albedo.
    vec3 baked_light = min((ambient_light * lightmap.r + direct_light * lightmap.g) * 2.0, vec3(1.0));
    ALBEDO = vec3(0.0);
    EMISSION = color * baked_light;
    ROUGHNESS = 1.0;
    SPECULAR = 0.0;
}
