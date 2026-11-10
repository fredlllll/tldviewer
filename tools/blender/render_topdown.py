"""Top-down unlit preview render for a TLD level GLB.

Usage:
    blender --background --factory-startup --python render_topdown.py -- \
        <input.glb> <output.png> [size] [margin] [samples]

Defaults: size=2048, margin=1.05, samples=16.

Rendering notes (measured on this machine, Blender 5.3.0):
  * Workbench and EEVEE crash headless (EXCEPTION_ACCESS_VIOLATION, no usable
    GPU context), so this script uses Cycles on the CPU.
  * Every material is replaced by an Emission shader, which gives a true unlit
    look without lights, shadows or GI.
  * Over a black background, alpha blending and additive blending produce the
    same result, so semi-transparent glow/corpse/branch textures read correctly.
"""

import bpy
import mathutils
import sys

# AssetRipper writes every glTF material as OPAQUE, so there is no alphaMode
# signal to honour. Instead, infer whether a material uses alpha from how much
# of its base-colour texture is transparent. Values outside this range are
# treated as opaque (nearly-clear or nearly-solid textures are too ambiguous:
# the former are often RGB textures with an empty alpha channel, and hiding
# real geometry is worse than showing a slightly wrong decal).
ALPHA_MIN_COVERAGE = 0.005
ALPHA_MAX_COVERAGE = 0.995
ALPHA_SAMPLE_SIZE = 64


def parse_args() -> tuple[str, str, int, float, int]:
    argv = sys.argv[sys.argv.index("--") + 1:]
    if len(argv) < 2:
        raise SystemExit(
            "usage: <input.glb> <output.png> [size] [margin] [samples]"
        )
    glb_path = argv[0]
    png_path = argv[1]
    size = int(argv[2]) if len(argv) > 2 else 2048
    margin = float(argv[3]) if len(argv) > 3 else 1.05
    samples = int(argv[4]) if len(argv) > 4 else 16
    return glb_path, png_path, size, margin, samples


def scene_bounds(scene) -> tuple[list[float], list[float], int]:
    mins = [1e30, 1e30, 1e30]
    maxs = [-1e30, -1e30, -1e30]
    mesh_count = 0
    for ob in scene.objects:
        if ob.type != 'MESH':
            continue
        mesh_count += 1
        mw = ob.matrix_world
        for corner in ob.bound_box:
            co = mw @ mathutils.Vector(corner)
            for i in range(3):
                mins[i] = min(mins[i], co[i])
                maxs[i] = max(maxs[i], co[i])
    return mins, maxs, mesh_count


def make_alpha_sampler():
    cache: dict[str, float] = {}

    def alpha_fraction(image) -> float:
        """Fraction of the texture that is transparent, 0.0 .. 1.0."""
        key = image.name
        if key in cache:
            return cache[key]

        fraction = 0.0
        try:
            small = image.copy()
            small.scale(ALPHA_SAMPLE_SIZE, ALPHA_SAMPLE_SIZE)
            pixels = list(small.pixels)
            transparent = sum(
                1 for i in range(3, len(pixels), 4) if pixels[i] < 0.5
            )
            fraction = transparent / (ALPHA_SAMPLE_SIZE * ALPHA_SAMPLE_SIZE)
            bpy.data.images.remove(small)
        except Exception:
            fraction = 0.0

        cache[key] = fraction
        return fraction

    return alpha_fraction


def unlit_materials(alpha_fraction) -> int:
    """Replace all shading with emission, keeping image alpha where relevant."""
    alpha_materials = 0

    for mat in bpy.data.materials:
        if not mat.use_nodes:
            mat.use_nodes = True
        nodes = mat.node_tree
        links = nodes.links

        output = next(
            (n for n in nodes.nodes if n.type == 'OUTPUT_MATERIAL'), None
        )
        if output is None:
            output = nodes.nodes.new("ShaderNodeOutputMaterial")

        principled = next(
            (n for n in nodes.nodes if n.type == 'BSDF_PRINCIPLED'), None
        )
        base_color = (
            principled.inputs.get("Base Color") if principled is not None else None
        )

        image_node = next(
            (
                n
                for n in nodes.nodes
                if n.type == 'TEX_IMAGE' and n.image is not None
            ),
            None,
        )
        image = image_node.image if image_node is not None else None

        emission = nodes.nodes.new("ShaderNodeEmission")
        emission.inputs["Strength"].default_value = 1.0

        if base_color is not None and base_color.is_linked:
            links.new(base_color.links[0].from_socket, emission.inputs["Color"])
        elif base_color is not None:
            emission.inputs["Color"].default_value = tuple(base_color.default_value)
        else:
            emission.inputs["Color"].default_value = (0.8, 0.8, 0.8, 1.0)

        surface = emission.outputs["Emission"]

        if image is not None and image_node is not None:
            coverage = alpha_fraction(image)
            if ALPHA_MIN_COVERAGE < coverage < ALPHA_MAX_COVERAGE:
                transparent = nodes.nodes.new("ShaderNodeBsdfTransparent")
                mix = nodes.nodes.new("ShaderNodeMixShader")
                links.new(image_node.outputs["Alpha"], mix.inputs["Fac"])
                links.new(transparent.outputs["BSDF"], mix.inputs[1])
                links.new(emission.outputs["Emission"], mix.inputs[2])
                surface = mix.outputs["Shader"]
                alpha_materials += 1

        links.new(surface, output.inputs["Surface"])

    return alpha_materials


def main() -> None:
    glb_path, png_path, size, margin, samples = parse_args()

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=glb_path)

    scene = bpy.context.scene
    mins, maxs, mesh_count = scene_bounds(scene)
    if mesh_count == 0:
        raise SystemExit("no meshes imported")

    cx = (mins[0] + maxs[0]) * 0.5
    cy = (mins[1] + maxs[1]) * 0.5
    extent = max(maxs[0] - mins[0], maxs[1] - mins[1]) * margin
    if extent <= 0:
        extent = 1.0

    # Top-down orthographic camera looking down -Z, +Y up.
    cam_data = bpy.data.cameras.new("TopDown")
    cam_data.type = 'ORTHO'
    cam_data.ortho_scale = extent
    cam_data.clip_start = 0.01
    cam_data.clip_end = (maxs[2] - mins[2]) + 10000.0

    cam = bpy.data.objects.new("TopDown", cam_data)
    scene.collection.objects.link(cam)
    cam.location = (cx, cy, maxs[2] + 100.0)
    cam.rotation_euler = (0.0, 0.0, 0.0)
    scene.camera = cam

    world = bpy.data.worlds.new("Black")
    world.use_nodes = True
    background = world.node_tree.nodes["Background"]
    background.inputs[0].default_value = (0.0, 0.0, 0.0, 1.0)
    background.inputs[1].default_value = 1.0
    scene.world = world

    alpha_materials = unlit_materials(make_alpha_sampler())

    scene.render.engine = 'CYCLES'
    scene.cycles.device = 'CPU'
    scene.cycles.samples = samples
    scene.cycles.use_adaptive_sampling = False
    scene.cycles.use_denoising = False
    scene.cycles.max_bounces = 4
    scene.cycles.diffuse_bounces = 0
    scene.cycles.glossy_bounces = 0
    scene.cycles.transmission_bounces = 0
    scene.cycles.transparent_max_bounces = 16
    scene.cycles.filter_width = 1.5

    scene.render.film_transparent = False
    scene.render.resolution_x = size
    scene.render.resolution_y = size
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = 'PNG'
    scene.render.image_settings.color_mode = 'RGB'
    scene.render.filepath = png_path

    # Standard transform: AgX/Filmic would wash out the flat colours.
    scene.view_settings.view_transform = 'Standard'
    scene.view_settings.look = 'None'
    scene.view_settings.exposure = 0.0
    scene.view_settings.gamma = 1.0

    print(
        f"PRECHECK meshes={mesh_count} mats={len(bpy.data.materials)} "
        f"alphaMats={alpha_materials} "
        f"bounds=({mins[0]:.1f},{mins[1]:.1f},{mins[2]:.1f}).."
        f"({maxs[0]:.1f},{maxs[1]:.1f},{maxs[2]:.1f}) extent={extent:.1f}"
    )

    bpy.ops.render.render(write_still=True)
    print("WROTE " + png_path)


if __name__ == "__main__":
    main()
