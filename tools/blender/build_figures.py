"""Generate the MiVic soldier models with Blender, headlessly.

    blender --background --python tools/blender/build_figures.py -- --out <dir>

Separate from build_vehicles.py because soldiers are a different problem from
vehicles. A tank is judged from above, at a glance, by its outline. A soldier is
1.8 metres tall in a game whose camera sits tens of metres away, so he is judged
by three things and nothing else: the shape of the helmet, the width of the
shoulders, and the diagonal of the weapon across his chest. Everything else here
exists to keep those three honest when the camera does come in close.

The part contract, which the renderer animates:

    LegLeft/LegRight    thighs, pivoting at the hip
    ShinLeft/ShinRight  shins, inheriting the thigh and bending at the knee
    ArmLeft/ArmRight    upper arms, swinging against the leg on the same side
    Body, Head          the parts that bob
    Coat/Armour/Pack    faction and role kit, riding the torso
    Rifle               whatever the figure is carrying
"""

import argparse
import math
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

# The mesh helpers, the material table and the exporter are shared with every
# other generator; there is exactly one definition of what "gunmetal" is.
from build_vehicles import MATERIALS, box, cylinder, dome, join, paint  # noqa: E402
from build_vehicles import clear_scene, export  # noqa: E402


# --------------------------------------------------------------------------
# One figure, in metres.
#
# Written against a 1.85 m man rather than as fractions of a parameter, because
# that is the only way to notice that a head is 24 cm across and a helmet is
# wider than the head it sits on. Shoulder width and bulk are the two knobs the
# factions actually differ by.
# --------------------------------------------------------------------------

HIP = 0.92
KNEE = 0.48
ANKLE = 0.09
SHOULDER = 1.40
HEAD_BASE = 1.47


def build_soldier(faction, kind, palette, kit=None, shoulders=0.42, bulk=1.0):
    """A low-poly soldier: two-part legs, two-part arms, helmet, webbing, weapon.

    `palette` carries the material table for this role and faction; `kit` decides
    which of the optional pieces are fitted.
    """
    root = bpy.data.objects.new(f"{faction}_{kind}", None)
    bpy.context.collection.objects.link(root)

    kit = kit or {}
    parts = []

    leg_half = shoulders * 0.26
    arm_x = (shoulders * 0.5) + (0.055 * bulk)

    # ---- legs, in two parts so the renderer can bend them at the knee --------
    for side, tag in ((-1, "Left"), (1, "Right")):
        thigh = box(
            f"Leg{tag}",
            (0.145 * bulk, 0.165 * bulk, HIP - KNEE),
            offset=(0.0, 0.0, -(HIP - KNEE) * 0.5),
        )
        thigh.location = (side * leg_half, 0.0, HIP)
        parts.append(thigh)
        paint(thigh, palette["legs"])

        shin = box(
            f"Shin{tag}",
            (0.125 * bulk, 0.145 * bulk, KNEE - ANKLE),
            offset=(0.0, 0.0, -(KNEE - ANKLE) * 0.5),
        )
        shin.parent = thigh
        shin.location = (0.0, 0.0, -(HIP - KNEE))
        parts.append(shin)
        paint(shin, palette["legs"])

        # A boot with a toe: a foot that is only as long as it is wide says the
        # figure is standing in a bucket.
        boot = box("Boot", (0.145 * bulk, 0.27, 0.115), offset=(0.0, 0.055, -(KNEE - ANKLE) - 0.055))
        boot.parent = shin
        parts.append(boot)
        paint(boot, palette["boots"], variation=0.04)

    # ---- torso: pelvis, waist, chest and shoulders as separate boxes ---------
    pelvis = box("Pelvis", (shoulders * 0.78, 0.20, 0.17), offset=(0.0, 0.0, HIP + 0.02))
    parts.append(pelvis)
    paint(pelvis, palette["legs"])

    waist = box("Waist", (shoulders * 0.72, 0.21, 0.13), offset=(0.0, 0.0, HIP + 0.17))
    parts.append(waist)
    paint(waist, palette["belt"])

    chest = box("Body", (shoulders * 0.92, 0.245 * bulk, 0.30), offset=(0.0, 0.0, HIP + 0.34))
    parts.append(chest)
    paint(chest, palette["body"])

    yoke = box("Yoke", (shoulders * 1.06, 0.22 * bulk, 0.10), offset=(0.0, 0.0, SHOULDER - 0.03))
    parts.append(yoke)
    paint(yoke, palette["body"])

    if kit.get("coat"):
        # A greatcoat: flared, belted, and hanging to below the knee. The single
        # widest thing on the figure, which is what makes a Σοβιετικοί squad read
        # as a solid block from above.
        coat = box("Coat", (shoulders * 1.02, 0.30 * bulk, 0.62), offset=(0.0, 0.0, HIP - 0.09))
        parts.append(coat)
        paint(coat, palette["body"], variation=0.05)

        skirt = box("CoatSkirt", (shoulders * 1.20, 0.34 * bulk, 0.22), offset=(0.0, 0.0, HIP - 0.30))
        parts.append(skirt)
        paint(skirt, palette["legs"], variation=0.05)

    if kit.get("armour"):
        plate = box("Armour", (shoulders * 0.80, 0.09, 0.26), offset=(0.0, 0.135 * bulk, HIP + 0.34))
        parts.append(plate)
        paint(plate, MATERIALS["steel"], variation=0.04)

        for side in (-1, 1):
            pad = box("ShoulderPad", (0.10, 0.17, 0.09), offset=(0.0, 0.0, 0.0))
            pad.location = (side * (arm_x + 0.02), 0.0, SHOULDER - 0.01)
            parts.append(pad)
            paint(pad, MATERIALS["steel"], variation=0.05)

    # ---- belt kit: pouches, and a pack behind -------------------------------
    for side in (-1, 1):
        pouch = box("Pouch", (0.10, 0.06, 0.11), offset=(0.0, 0.0, 0.0))
        pouch.location = (side * shoulders * 0.26, 0.115, HIP + 0.10)
        parts.append(pouch)
        paint(pouch, palette["pack"], variation=0.06)

    if kit.get("pack"):
        pack = box("Pack", (shoulders * 0.66, 0.15, 0.34), offset=(0.0, -0.155 * bulk, HIP + 0.38))
        parts.append(pack)
        paint(pack, palette["pack"], variation=0.05)

        if kit.get("antenna"):
            antenna = box("Antenna", (0.014, 0.014, 0.46), offset=(0.0, 0.0, 0.23))
            antenna.location = (shoulders * 0.34, -0.16 * bulk, HIP + 0.52)
            parts.append(antenna)
            paint(antenna, MATERIALS["gun_dark"])

    # ---- arms, in two parts, with a hand at the end -------------------------
    for side, tag in ((-1, "Left"), (1, "Right")):
        upper = box("Arm" + tag, (0.105 * bulk, 0.125 * bulk, 0.28), offset=(0.0, 0.0, -0.14))
        upper.location = (side * arm_x, 0.0, SHOULDER)
        parts.append(upper)
        paint(upper, palette["body"])

        fore = box("Forearm" + tag, (0.095 * bulk, 0.11 * bulk, 0.26), offset=(0.0, 0.0, -0.13))
        fore.parent = upper
        fore.location = (0.0, 0.0, -0.28)
        parts.append(fore)
        paint(fore, palette["body"])

        hand = box("Hand" + tag, (0.085, 0.10, 0.09), offset=(0.0, 0.0, -0.045))
        hand.parent = fore
        hand.location = (0.0, 0.0, -0.26)
        parts.append(hand)
        paint(hand, palette["gloves"], variation=0.04)

    # ---- head: neck, skull, jaw, and the helmet that names the faction ------
    neck = box("Neck", (0.10, 0.10, 0.09), offset=(0.0, 0.0, 0.045))
    neck.location = (0.0, 0.0, HEAD_BASE)
    parts.append(neck)
    paint(neck, palette["skin"])

    head = box("Head", (0.165, 0.195, 0.21), offset=(0.0, 0.0, 0.105))
    head.parent = neck
    parts.append(head)
    paint(head, palette["skin"])

    # A jaw in shadow, so the head is not one flat pale cube.
    jaw = box("Jaw", (0.150, 0.055, 0.075), offset=(0.0, 0.10, 0.035))
    jaw.parent = head
    parts.append(jaw)
    paint(jaw, palette["skin_shade"], variation=0.04)

    helmet = kit.get("helmet", "dome")
    crown = HEAD_BASE + 0.185

    if helmet == "cap":
        # A field cap: a crowned band with a peak at the front. Nothing else in
        # the game wears one, and at twenty pixels it is the whole difference.
        cap = box("Helmet", (0.205, 0.225, 0.105), offset=(0.0, 0.0, 0.045))
        cap.location = (0.0, 0.0, crown)
        parts.append(cap)
        paint(cap, palette["helmet"])

        peak = box("Peak", (0.175, 0.10, 0.022), offset=(0.0, 0.155, 0.0))
        peak.parent = cap
        parts.append(peak)
        paint(peak, MATERIALS["gun_dark"])
    else:
        shell = dome("Helmet", 1.0, (0.108, 0.122, 0.105), segments=14, rings=4)
        shell.location = (0.0, 0.0, crown + 0.015)
        parts.append(shell)
        paint(shell, palette["helmet"])

        # A rim: the difference between a helmet and a bowl on someone's head.
        rim = box("HelmetRim", (0.245, 0.275, 0.030), offset=(0.0, 0.0, 0.0))
        rim.location = (0.0, 0.0, crown + 0.015)
        parts.append(rim)
        paint(rim, palette["helmet"], variation=0.03)

    if helmet == "visor":
        visor = box("Visor", (0.155, 0.06, 0.055), offset=(0.0, 0.085, 0.135))
        visor.location = (0.0, 0.0, HEAD_BASE)
        parts.append(visor)
        paint(visor, MATERIALS["glass"], variation=0.03)
    elif helmet != "cap":
        eyes = box("Visor", (0.135, 0.045, 0.030), offset=(0.0, 0.085, 0.130))
        eyes.location = (0.0, 0.0, HEAD_BASE)
        parts.append(eyes)
        paint(eyes, MATERIALS["gun_dark"], variation=0.03)

    # ---- the weapon --------------------------------------------------------
    # Four boxes rather than one: a receiver, a barrel, a magazine and a stock.
    # A single bar across the chest reads as a plank, and this is the part of a
    # soldier a player looks at most.
    weapon = kit.get("weapon", "rifle")

    body_len = {"carbine": 0.34, "rifle": 0.42, "launcher": 0.52}.get(weapon, 0.42)
    barrel_len = {"carbine": 0.30, "rifle": 0.46, "launcher": 0.0}.get(weapon, 0.46)

    gun = box("Rifle", (0.055, 0.085, body_len), offset=(0.0, 0.0, 0.0))
    gun.location = (0.0, 0.235 * bulk, HIP + 0.30)
    gun.rotation_euler = (0.0, math.radians(-32.0), 0.0)
    parts.append(gun)
    paint(gun, MATERIALS["gun"], variation=0.04)

    if weapon == "launcher":
        tube = cylinder("Launcher", 0.085, 0.62, segments=12, axis="z")
        tube.parent = gun
        tube.location = (0.0, 0.0, 0.10)
        parts.append(tube)
        paint(tube, MATERIALS["gun_dark"], variation=0.04)

        sight = box("Sight", (0.05, 0.07, 0.10), offset=(0.0, -0.10, 0.12))
        sight.parent = gun
        parts.append(sight)
        paint(sight, MATERIALS["steel"])
    else:
        barrel = box("Barrel", (0.028, 0.028, barrel_len), offset=(0.0, 0.0, (body_len * 0.5) + (barrel_len * 0.5)))
        barrel.parent = gun
        parts.append(barrel)
        paint(barrel, MATERIALS["gun_dark"], variation=0.03)

        magazine = box("Magazine", (0.036, 0.075, 0.16), offset=(0.0, 0.02, -0.02))
        magazine.parent = gun
        magazine.rotation_euler = (math.radians(14.0), 0.0, 0.0)
        parts.append(magazine)
        paint(magazine, MATERIALS["gun_dark"], variation=0.03)

        stock = box("Stock", (0.045, 0.075, 0.20), offset=(0.0, -0.01, -(body_len * 0.5) - 0.09))
        stock.parent = gun
        parts.append(stock)
        paint(stock, MATERIALS["crate"], variation=0.04)

        if kit.get("bayonet"):
            blade = box("Bayonet", (0.018, 0.026, 0.26), offset=(0.0, 0.0, body_len * 0.5 + barrel_len + 0.13))
            blade.parent = gun
            parts.append(blade)
            paint(blade, MATERIALS["panel"], variation=0.03)

    join(root, parts)
    return root


# --------------------------------------------------------------------------
# Palettes
#
# Keys are the material names build_soldier asks for. The paint mask in each
# entry's alpha decides how much of the faction colour covers that piece, which
# is what keeps a red army from being a red silhouette with no shape.
# --------------------------------------------------------------------------

FLESH = MATERIALS["flesh"]
FLESH_SHADE = (0.52, 0.39, 0.30, 0.00)


def infantry(faction):
    """The line infantryman: one per faction, and visibly not the same man."""
    if faction == "soviet":
        # Greatcoat, pot helmet, slung rifle. Broad and low — a wall of them.
        return (
            {"body": (0.34, 0.35, 0.30, 0.60),
             "belt": (0.22, 0.20, 0.16, 0.30),
             "legs": (0.25, 0.26, 0.23, 0.44),
             "boots": (0.12, 0.11, 0.10, 0.06),
             "gloves": (0.16, 0.15, 0.14, 0.10),
             "skin": FLESH,
             "skin_shade": FLESH_SHADE,
             "helmet": (0.33, 0.35, 0.29, 0.14),
             "pack": MATERIALS["crate"]},
            {"coat": True, "helmet": "dome", "weapon": "rifle", "pack": True},
            0.46,
            1.06)

    if faction == "chinese":
        # Light tunic, field cap, bayonet. Narrower and taller: mass production.
        return (
            {"body": (0.44, 0.45, 0.36, 0.54),
             "belt": (0.24, 0.22, 0.16, 0.25),
             "legs": (0.30, 0.31, 0.26, 0.36),
             "boots": (0.17, 0.15, 0.12, 0.06),
             "gloves": (0.20, 0.18, 0.15, 0.10),
             "skin": FLESH,
             "skin_shade": FLESH_SHADE,
             "helmet": (0.50, 0.47, 0.31, 0.42),
             "pack": (0.38, 0.33, 0.22, 0.12)},
            {"helmet": "cap", "weapon": "carbine", "bayonet": True, "pack": True},
            0.40,
            0.94)

    # Δυτικοί: body armour, visor, and a pack big enough to live out of.
    return (
        {"body": (0.29, 0.32, 0.37, 0.54),
         "belt": (0.20, 0.21, 0.24, 0.28),
         "legs": (0.22, 0.24, 0.28, 0.34),
         "boots": (0.13, 0.13, 0.14, 0.06),
         "gloves": (0.18, 0.19, 0.21, 0.12),
         "skin": FLESH,
         "skin_shade": FLESH_SHADE,
         "helmet": (0.24, 0.27, 0.30, 0.18),
         "pack": (0.24, 0.26, 0.28, 0.12)},
        {"armour": True, "helmet": "visor", "weapon": "rifle", "pack": True, "antenna": True},
        0.48,
        1.16)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description="Generate MiVic soldier models.")
    parser.add_argument("--out", required=True, help="Directory to write .glb files into.")
    args = parser.parse_args(argv)

    os.makedirs(args.out, exist_ok=True)
    written = []

    def emit(name, builder):
        clear_scene()
        builder()
        path = os.path.join(args.out, f"{name}.glb")
        export(path)
        written.append(path)

    for faction in ("soviet", "chinese", "western"):
        palette, kit, shoulders, bulk = infantry(faction)

        emit(f"{faction}_infantry", lambda f=faction, p=palette, k=kit, s=shoulders, b=bulk:
             build_soldier(f, "infantry", p, k, s, b))

    # ---- specialists, on the same rig with different kit -------------------
    emit("soviet_commissar", lambda: build_soldier(
        "soviet", "commissar",
        {"body": (0.32, 0.32, 0.33, 0.62),
         "belt": (0.30, 0.24, 0.16, 0.35),
         "legs": (0.22, 0.22, 0.24, 0.48),
         "boots": (0.15, 0.12, 0.10, 0.06),
         "gloves": (0.18, 0.15, 0.12, 0.10),
         "skin": FLESH,
         "skin_shade": FLESH_SHADE,
         "helmet": (0.46, 0.13, 0.11, 0.30),
         "pack": MATERIALS["crate"]},
        {"coat": True, "helmet": "cap", "weapon": "carbine", "pack": True, "antenna": True},
        0.44, 1.02))

    emit("chinese_robot", lambda: build_soldier(
        "chinese", "robot",
        {"body": MATERIALS["robot"],
         "belt": MATERIALS["gun_dark"],
         "legs": MATERIALS["robot_leg"],
         "boots": MATERIALS["steel"],
         "gloves": MATERIALS["steel"],
         "skin": MATERIALS["gun_dark"],
         "skin_shade": MATERIALS["gun_dark"],
         "helmet": MATERIALS["coil"],
         "pack": MATERIALS["steel"]},
        {"armour": True, "helmet": "visor", "weapon": "launcher", "pack": True, "antenna": True},
        0.50, 1.10))

    emit("western_mercenary", lambda: build_soldier(
        "western", "mercenary",
        {"body": (0.32, 0.35, 0.40, 0.52),
         "belt": (0.34, 0.26, 0.14, 0.30),
         "legs": (0.25, 0.27, 0.31, 0.38),
         "boots": (0.18, 0.15, 0.12, 0.06),
         "gloves": (0.20, 0.18, 0.16, 0.12),
         "skin": FLESH,
         "skin_shade": FLESH_SHADE,
         "helmet": (0.28, 0.31, 0.34, 0.20),
         "pack": (0.34, 0.30, 0.24, 0.12)},
        {"armour": True, "helmet": "visor", "weapon": "launcher", "pack": True},
        0.52, 1.24))

    emit("western_stalker", lambda: build_soldier(
        "western", "stalker",
        {"body": (0.20, 0.22, 0.26, 0.24),
         "belt": (0.14, 0.15, 0.18, 0.18),
         "legs": (0.15, 0.17, 0.20, 0.18),
         "boots": (0.10, 0.10, 0.12, 0.06),
         "gloves": (0.12, 0.13, 0.16, 0.10),
         "skin": (0.36, 0.28, 0.22, 0.00),
         "skin_shade": (0.24, 0.19, 0.15, 0.00),
         "helmet": (0.13, 0.15, 0.19, 0.12),
         "pack": (0.14, 0.16, 0.19, 0.10)},
        {"helmet": "visor", "weapon": "carbine", "pack": True},
        0.42, 0.98))

    for path in written:
        size = os.path.getsize(path)
        print(f"wrote {os.path.basename(path)}  ({size / 1024:.1f} KB)")

    print(f"done: {len(written)} figures")


if __name__ == "__main__":
    main()
