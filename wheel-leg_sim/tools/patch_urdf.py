# Reproduces Assets/Robots/go2w/go2w_description.urdf from the upstream Unitree file.
#
#   python tools/patch_urdf.py <upstream go2w_description.urdf> <output .urdf>
#
# Upstream source, pinned:
#   https://github.com/unitreerobotics/unitree_ros/tree/
#     f3772ce54c56ef2d34c6aee8100bc768896c7d19/robots/go2w_description/urdf
#
# Verified 2026-07-30. Check these before trusting a re-download; the patch asserts on
# structure, but only the hash proves it is the same upstream revision.
#   upstream go2w_description.urdf  24479 bytes
#     sha256 F5940CBEF9BE28B76C075A94ED535C9C8C22C89A79B54EC8C854DC9F023C1AB8
#   this script's output, which is what Assets/Robots/go2w/go2w_description.urdf holds
#     sha256 7569FF91E2BC07D0C12FAD6E5F9F7128F9A11AD06A3C4448566E2BD91FB153F7
#
# Two changes are made and nothing else; the rest of the file is byte-for-byte upstream.
# Both exist because of a defect that was measured, not because of a preference.
#
# 1. The four wheel (*_foot) collisions become cylinder primitives.
#    Upstream gives them mesh colliders. A convex mesh wheel rolls badly and costs far more
#    than a primitive, and the whole point of the wheel is its rolling contact.
#    The cylinder is measured from the wheel mesh itself, not chosen:
#      radius (x/z extent) = 0.08596384
#      width  (y extent)   = 0.05181491  -> y in [0.02218461, 0.07399952] on the left side
#      centre |y|          = 0.04809207
#    A URDF <cylinder> is a +Z axis; the wheel joint axis is +Y, hence rpy roll = pi/2.
#
# 2. Links with no <inertial>, or with mass 0, get a negligible explicit mass.
#    Unity gives an ArticulationBody a default mass of 1 kg when the URDF says nothing. The
#    eight *_calflower/*_calflower1 shin links plus the imu and radar frames are all massless
#    upstream, so the imported robot weighed 27.7 kg against the URDF's 19.5 kg: 42% heavy.
#    After this patch the imported total is 19.533 kg, matching the URDF sum.
#
# The script fails loudly if the upstream file does not match these expectations rather than
# writing a partly-patched robot: a silently wrong URDF is what produced the 42% mass error.
import io
import re
import sys

if len(sys.argv) != 3:
    sys.exit("usage: patch_urdf.py <upstream.urdf> <output.urdf>")
SRC, DST = sys.argv[1], sys.argv[2]

RADIUS = 0.08596384
LENGTH = 0.05181491
OFFSET = 0.04809207

s = io.open(SRC, encoding="utf-8").read()

WHEEL_LINKS = {"FL_foot": +1, "FR_foot": -1, "RL_foot": +1, "RR_foot": -1}

patched = 0
for link, sign in WHEEL_LINKS.items():
    pat = re.compile(
        r'(<link name="' + link + r'">.*?)'
        r'<collision>\s*<origin[^>]*/>\s*<geometry>\s*<mesh[^>]*/>\s*</geometry>\s*</collision>'
        r'(.*?</link>)',
        re.S,
    )
    new_col = (
        '<collision>\n'
        '      <origin rpy="1.5707963267948966 0 0" xyz="0 %.8f 0" />\n'
        '      <geometry>\n'
        '        <cylinder radius="%.8f" length="%.8f" />\n'
        '      </geometry>\n'
        '    </collision>'
    ) % (sign * OFFSET, RADIUS, LENGTH)
    s, n = pat.subn(lambda m: m.group(1) + new_col + m.group(2), s, count=1)
    patched += n

if patched != 4:
    sys.exit("expected 4 wheel collision replacements, made %d" % patched)

if "<mesh" in re.sub(r"<visual>.*?</visual>", "", s, flags=re.S):
    sys.exit("a mesh collider survived the patch")

NEGLIGIBLE = (
    '<inertial>\n'
    '      <origin xyz="0 0 0" rpy="0 0 0" />\n'
    '      <mass value="0.001" />\n'
    '      <inertia ixx="1e-6" ixy="0" ixz="0" iyy="1e-6" iyz="0" izz="1e-6" />\n'
    '    </inertial>\n    '
)

added, zeroed = 0, 0
out = []
pos = 0
for m in re.finditer(r'<link name="([^"]+)">(.*?)</link>', s, re.S):
    name, body = m.group(1), m.group(2)
    out.append(s[pos:m.start()])
    mass = re.search(r'<mass value="([0-9.eE+-]+)"\s*/>', body)
    if mass is None:
        body = NEGLIGIBLE + body.lstrip()
        added += 1
    elif float(mass.group(1)) <= 0.0:
        body = body[:mass.start()] + '<mass value="0.001" />' + body[mass.end():]
        zeroed += 1
    out.append('<link name="%s">%s</link>' % (name, body))
    pos = m.end()
out.append(s[pos:])
s = "".join(out)

if added != 8 or zeroed != 2:
    sys.exit("expected 8 missing-inertial and 2 zero-mass links, got %d and %d" % (added, zeroed))

io.open(DST, "w", encoding="utf-8", newline="\n").write(s)
print("patched %d wheel collisions, %d missing inertials, %d zero masses -> %s"
      % (patched, added, zeroed, DST))
