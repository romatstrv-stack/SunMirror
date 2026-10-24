# Sun Mirror ☀️🪞

Your mate is inching along a ledge, two steps from the top. What if the sun
got in their eyes — right now?

![Sun Mirror in action](https://raw.githubusercontent.com/romatstrv-stack/SunMirror/master/media/sunmirror-demo.gif)

Pick up a frisbee and hold left mouse button. A bright spot of sunlight crawls
across whatever you point at. Walk it up a teammate's body and into their eyes:
hands fly up over the face, spiral eyes, dazed mouth, and they can't move or
look away. Hold it there and they drop.

Then they get up, and the same target can't be blinded again for four seconds.
A frisbee should not be able to switch a person off.

## It works on people who don't have the mod

| | with the mod | vanilla, no mod |
|---|---|---|
| Hands up over the face | yes | **yes** |
| Head flinches back | yes | **yes** |
| Falls over after five seconds | yes | **yes** |
| Spiral eyes, dazed mouth | yes | no |
| Frozen in place, look locked | yes | no |
| Blinding flare on their screen | yes | no |

The pose, the flinch and the fall travel through the game's own networking, so
a stranger who has never heard of this mod still reacts — and still falls. Only
the one holding the mirror needs it installed.

## Install

Mod manager (r2modman / Gale / Thunderstore Mod Manager): find **Sun Mirror**,
click Install, launch modded. Manual: drop `Roman.SunMirror.dll` into
`BepInEx/plugins/`.

**It needs daylight.** The beam only works when the sun actually reaches you:
not at night, not in shade, not indoors. That is deliberate.

## Feedback

Something broken, or an idea?
**[Open an issue](https://github.com/romatstrv-stack/SunMirror/issues)** — it is
the only place I can hear you. A bug report with your `BepInEx/LogOutput.log`
attached is worth ten without it.

---

<details>
<summary><b>Configuration</b></summary>

`BepInEx/config/Roman.SunMirror.cfg`, or an in-game config manager.

- **Spot** — `Size`, `SpreadPerMeter`, `SpriteBrightness`, `ContrastRing`:
  size, punch and edge contrast of the light spot.
- **Emote** — `MaxBlindSeconds`, `FallAtMaxBlind`, `EyesOnly`, `BlindSfxName`:
  how long the daze lasts, whether the victim drops, whether only eye hits
  count, and the sound on contact.
- **Emote** — `VanillaFlinchForce`: the head flinch that reaches players
  without the mod. Keep it low; high values knock people over.
- **Reflection** — `RealMirror`, `RealMirrorResolution`: the actual mirror
  surface. Lower the resolution if you are tight on performance.
- **Model** — `FlashEmission`, `FlashPulseAmount`: how brightly the mirror
  glows while you aim. Push the emission too high and the game's auto-exposure
  starts chasing it, which makes the whole scene pulse.
- **Debug** — `DebugKeys`: developer keys, off by default.

</details>

<details>
<summary><b>How it is built, and what it cannot do</b></summary>

The mirror is the game's own frisbee, reskinned. It is held low with both hands
so it never blocks your view, and the face turned towards your target is a real
reflective surface rendered with the game's own mirror shader. You see the back
of it, they see the glare — like a real signal mirror.

The beam spreads by 9 mm per metre. That is the real divergence of a sunbeam off
a flat mirror: the sun is 0.53° across, so at 60 m the spot is about a metre
wide. It only works in actual sunlight for the same reason.

Spiral eyes, the dazed mouth and the movement lock are delivered by this mod's
own networking, so a player without the mod will not get them. That is a limit
of how Photon works, and it should be one: delivering the rest would mean
running code on someone else's machine, and nothing should do that unless they
installed it themselves.

A victim holding an item will not show the hands-to-face pose — the item-hold
animation overrides it.

Built with [PEAKLib](https://github.com/PEAKModding/PEAKLib) and BepInEx. No
third-party models or textures are bundled: everything is either generated at
runtime or reused from the game's own assets.

</details>
