"""Editing the images MPFB loaded, before the exporter embeds them.

MPFB builds a node tree per asset and points it at the asset pack's own textures; the
glTF exporter then embeds those images in the glb. So an edit made here, on the image
datablock in the Blender session, is an edit to what the figure wears — no sidecar file,
no second place for the art to be, and nothing for the game to look up.

The two things done here are recolouring a garment and finding which image a garment's
material actually uses. Both are small; both exist because guessing was worse:

  * Finding the base colour by filename pattern is a guess. `bpy.data.images` held eight
    images once the garments were on, and "first name ending in .png" picked an eyeball.
  * Finding it by node tracing is also a guess, because MPFB's game-engine material is a
    node group and the base colour enters it through the group's own inputs.
  So the rule is: the images this object's own materials name, and among those the one the
  asset pack calls a `diffuse`. That is checkable by looking in the pack.
"""

import numpy as np

#: The luminance coefficients (Rec. 709), for keeping shading while replacing hue.
LUMA = (0.2126, 0.7152, 0.0722)


def material_images(obj):
    """Every image referenced by the node trees of this object's materials."""
    found = []
    for slot in obj.material_slots:
        material = slot.material
        if material is None or not material.use_nodes or material.node_tree is None:
            continue
        for node in material.node_tree.nodes:
            if node.type != "TEX_IMAGE" or node.image is None:
                continue
            if node.image not in found:
                found.append(node.image)
    return found


def material_image(obj, prefer="diffuse"):
    """The base-colour image of this object's material, or None.

    `prefer` is matched against the image's own name, which is the asset pack's name for
    it and the only label there is. When nothing matches and exactly one image is present,
    that one is the answer; when several are present and none matches, this returns None
    and says so, because picking one would be a coin toss that renders as a wrong garment.
    """
    images = material_images(obj)
    if not images:
        return None

    matching = [i for i in images if prefer in i.name.lower()]
    if len(matching) == 1:
        return matching[0]
    if len(images) == 1:
        return images[0]

    print(f"  {obj.name}: {len(images)} images and {len(matching)} match '{prefer}': "
          + ", ".join(i.name for i in images))
    return None


def _read(image):
    buffer = np.empty(image.size[0] * image.size[1] * image.channels, dtype=np.float32)
    image.pixels.foreach_get(buffer)
    return buffer.reshape(-1, image.channels)


def recolour(image, target, detail=0.30):
    """Replace an image's hue with `target`, keeping its light and shade.

    A flat fill would throw away the weave, the seams and the folds that make a garment
    read as cloth rather than as painted-on colour — and the asset pack's own maps have
    that detail already, measured from real cloth. So each texel keeps its luminance and
    only its colour changes: `detail` is how much of the original light and shade survives
    as a multiplier on the target, and 0.30 means a range of about ±30 per cent.
    """
    pixels = _read(image)
    if pixels.shape[1] < 3:
        return 0

    rgb = pixels[:, :3]
    luminance = rgb @ np.array(LUMA, dtype=np.float32)
    factor = (1.0 - detail) + detail * 2.0 * luminance
    painted = np.clip(np.array(target, dtype=np.float32)[None, :] * factor[:, None], 0.0, 1.0)
    pixels[:, :3] = painted

    image.pixels.foreach_set(pixels.reshape(-1))
    image.update()
    return int(pixels.shape[0])
