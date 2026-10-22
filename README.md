# Sun Mirror ☀️🪞

Catch the sun with a frisbee and blind your teammate with the reflection.

Pick up a frisbee, hold left mouse button, and a bright spot of sunlight crawls across
whatever you point at. Walk it up a teammate's body to their eyes and they get dazzled:
hands up over the face, frozen in that pose, spiral eyes, dazed mouth, unable to move
or look away until you point the mirror somewhere else.

Nobody falls. Nobody dies. It is a banana-peel prank, not a weapon.

## What the victim actually sees

| | with the mod | vanilla, no mod |
|---|---|---|
| Hands up over the face | yes | **yes** |
| Head flinches back | yes | **yes** |
| Spiral eyes, dazed mouth | yes | no |
| Frozen in place, look locked | yes | no |
| Blinding flare on their screen | yes | no |

The pose and the flinch travel through the game's own networking, so strangers who have
never heard of this mod still react visibly. Everything else needs the mod on their side.

**There is no way around that, and there should not be.** Delivering the rest means
running code on someone else's machine, and nothing should ever do that unless they
installed it themselves. If you want the whole effect, everyone installs the mod.

## Requirements

- BepInEx (BepInExPack_PEAK) — your mod manager pulls this in automatically.
- Daylight. The beam only works when the sun actually reaches you: not at night,
  not in shade, not indoors. That is deliberate.

## Install

Use a mod manager (r2modman / Gale / Thunderstore Mod Manager): find **Sun Mirror**,
click Install, launch modded.

Manual: drop `Roman.SunMirror.dll` into `BepInEx/plugins/`. Nothing else is needed.

## Configuration

`BepInEx/config/Roman.SunMirror.cfg`, or an in-game config manager.

- **Spot** — `Size`, `SpreadPerMeter`, `SpriteBrightness`: size and punch of the light spot.
- **Emote** — `BlindHoldSeconds`, `BlindMouthName`, `PoseFreezeAt`: how long the daze lasts,
  which mouth the victim gets, and which frame of the animation the pose holds on.
- **Emote** — `VanillaFlinch`, `VanillaFlinchForce`: the head flinch that reaches players
  without the mod. Set the force low; high values knock people over.
- **Reflection** — `RealMirror`, `RealMirrorResolution`: the actual mirror surface.
  Lower the resolution if you are tight on performance.
- **Debug** — `DebugKeys`: developer keys, off by default.

## Notes

The mirror is the game's own frisbee, reskinned. It is held low with both hands so it
never blocks your view, and the face turned towards your target is a real reflective
surface rendered with the game's own mirror shader. You see the back of it, they see the
glare — like a real signal mirror.

## Credits

Built with [PEAKLib](https://github.com/PEAKModding/PEAKLib) and BepInEx.
No third-party models or textures are bundled: everything is either generated at runtime
or reused from the game's own assets.

## Feedback

Something broken, or an idea? Open an issue:
**https://github.com/romatstrv-stack/SunMirror/issues**

Thunderstore has no comments, so that repo is the only place I can hear you.
Bug reports with your `BepInEx/LogOutput.log` attached are the most useful.
