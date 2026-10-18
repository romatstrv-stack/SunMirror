# Sun Mirror ☀️🪞

Take out a little hand-mirror, catch the sun, and blast a **sunbeam** into your teammate's eyes — they stumble and faceplant. A friendly, banana-peel-style prank. No real damage, effect is short.

> **Only *you* need the mod.** Your victims — even random strangers with a vanilla game — get knocked down anyway, because the ragdoll is sent through the game's own networking.

---

## How to use

| Key | Action |
|-----|--------|
| **G** | Take out / put away the mirror (it appears in your hand) |
| **F** | Use it — reflect the sun where you're looking |

Aim at a teammate, press **F**, and watch them tumble. Keys are rebindable in the config.

## What others see

- **Everyone (no mod needed):** the victim ragdolls / faceplants.
- **Players with the mod (and you):** the mirror model in your hand + the visible sunbeam. Great for clips.

That's just how multiplayer rendering works — custom visuals only draw for people who have the mod, but the *effect* reaches everyone.

## Install

Use a mod manager (**r2modman** / **Gale** / Thunderstore Mod Manager): find **Sun Mirror**, click Install, launch modded. BepInEx is pulled in automatically.

Manual: drop `Roman.SunMirror.dll` **and** `mirror.glb` together into `BepInEx/plugins/`.

## Configuration

Edit `BepInEx/config/Roman.SunMirror.cfg` (or use the in-game config manager):

- **Controls** — `EquipKey` (G), `UseKey` (F)
- **Beam** — `Range`, `Width`, `Seconds`
- **Effect** — `FallSeconds`, `FallShake`, and optional `ApplyStatuses` (adds a blinding Spores + Drowsy — works best when the target also has the mod)
- **Model** — `HeldScale`, `HeldOffsetX/Y/Z`, `HeldYaw/Pitch/Roll` to fine-tune how the mirror sits in your hand

## Credits

- Mirror 3D model by **POLOOOO** (Sketchfab), licensed **CC BY 4.0**. *(Add the exact model link here before release.)*
- Built with [PEAKLib](https://github.com/PEAKModding/PEAKLib) and BepInEx.

## Notes

Early prototype — feedback welcome. It's a for-fun troll mod; be nice out there. 🍌
