# B&D City

A walkable, stylised Rotterdam in VR where gifts travel through the city as bubbles. Built by our team for the **B&D City challenge** at the HR Hackathon 2026 (Hogeschool Rotterdam), as a standalone demo on Meta Quest 3.

![A gift bubble circling the player in Blaak](docs/images/bubble-arrives.jpg)

## What you can do

- **Walk four neighbourhoods**: Wijnhaven 107 (Hogeschool Rotterdam), Lijnbaan, Blaak (Markthal and Cube Houses) and Stadhuis. Travel with "Go to…" in the menu, walk with the joystick or teleport.
- **Receive gifts**: a personal bubble flies in, circles you, and pops into a gift (a message, an accessory or a clothing piece). Ambient bubbles fly to other visitors so the city feels busy.
- **Send gifts**: pick a person, a gift type, an item and a message; your bubble flies to them and pops above their head.
- **Change outfit**: swap clothing pieces and colours on your own avatar, shown as a mirror copy in front of you.
- **Meet Lotte**: the tutorial guide walks up on first arrival and teaches the controls; afterwards she waits at a help stand in every area.
- **Play Bubble Sky**: a 90-second mini game on a platform above Blaak, with combos, golden bubbles and a top-10 scoreboard.

The scope was a concept demo: no backend, accounts or networking. The other people in the city are bots, and the fashion website (`fashion-site/`) is a clickable mock-up.

### What it looks like

| | |
| --- | --- |
| ![Wijnhaven 107](docs/images/area-wijnhaven-107.jpg) Wijnhaven 107 | ![Lijnbaan](docs/images/area-lijnbaan.jpg) Lijnbaan |
| ![Blaak](docs/images/area-blaak.jpg) Blaak, with the Markthal | ![Stadhuis](docs/images/area-stadhuis.jpg) Stadhuis |
| ![Lotte](docs/images/lotte-tutorial.jpg) Lotte, the tutorial guide | ![Change outfit](docs/images/menu-outfit.jpg) Change outfit |

## The bubble feature

Gifts never just appear in your inventory: each one travels through the city as a bubble, so giving is something you see and do. Everything is animated in code (no physics, no Bloom, one small particle system per pop), which keeps it at 72 fps on the Quest.

![Gift bubble flow: receiving and sending end in the same reveal](docs/images/bubble-flow.png)

### Receiving a gift

1. **Spawn**: a bubble appears about 8 m away, 1 m above eye height, within 60° of where you look.
2. **Fly in**: it flies at up to 2 m/s, brakes in the last 1.5 m and stops about 1 m in front of you, slightly below eye level.
3. **Circle**: it slowly orbits your head (one lap per 15 s) and follows you when you walk.
4. **Pop**: point a controller at it and press trigger or grip. Aim assist (9°, up to 6 m) and a 0.35 s grace period mean a lamp post or a fast bubble doesn't block the pop. On desktop you click it.
5. **Burst and gather**: the shell swells 30% and vanishes; tiny bubbles burst outward, then swirl together into one point.
6. **Reveal**: "From: sender", the message on a dark panel, and the item slowly spinning. It stays a few seconds, then shrinks away.

| | |
| --- | --- |
| ![Bubble flying in](docs/images/bubble-flying.jpg) Flying in across Blaak | ![Gift reveal](docs/images/gift-reveal.jpg) The reveal (a necklace) |

![The pop, frame by frame](docs/images/pop-sequence.jpg)

*The pop, frame by frame: the bubble, the burst into tiny bubbles, the swirl, and the gift (here a watch ring) appearing where they meet.*

![The NY cap revealed](docs/images/reveal-ny-cap.jpg)

*Another reveal: the 3D-scanned NY cap.*

### Bubbles for everyone else

- **Ambient bubbles**: 6 at a time fly from out of your view to bots (60% of the time) or random points within 15 m, and pop there.
- **Bots**: five stand-in visitors (Sanne, Daan, Noor, Milan, Fatima) wander near you, each with a name tag and a colour.

The bots and ambient bubbles were set up this way for the demo. In the full concept, B&D City is multiplayer: the people walking around are real visitors, and the city fills with bubbles of people sending gifts to each other.

![Ambient bubbles and bots](docs/images/ambient-bubbles.jpg)

### Sending a gift

Open the menu (Y on the left controller, M on keyboard) → **Send gift** → pick a person → *Message only*, *Accessory* or *Clothing* → pick the item → pick a message → confirm. Your bubble flies to them, pops above their head and shows the same reveal there.

| | | |
| --- | --- | --- |
| ![Send a gift menu](docs/images/menu-send-gift.jpg) Pick who gets it | ![Bubble on its way](docs/images/gift-sent.jpg) On its way to Sanne | ![Reveal above the bot](docs/images/gift-to-bot.jpg) Revealed above her head |

### Bubble Sky mini game

From the menu you are lifted onto a platform high above Blaak. Bubbles come from every side for 90 s; quick pops build a combo (up to ×4), and rare golden bubbles are worth 5. The top 10 scores are saved on the headset.

![Bubble Sky](docs/images/bubble-sky.jpg)

### Gift data

Each gift is a ScriptableObject (right-click → Create → B&D City → Gift): sender, recipient, message, type (none, accessory, clothing), an optional 3D model and a colour. The menu lists these assets, so adding a gift needs no code. Demo gifts include three jewellery pieces modelled from photos (watch ring, onyx ring, sacred-heart necklace), two 3D-scanned hats (a fisherman's hat and a NY cap), and a placeholder shirt.

## Tech stack

| Layer | What we used | Version / notes |
| --- | --- | --- |
| Engine | Unity | 6000.6.0f1 |
| Rendering | Universal Render Pipeline | 17.6.0; Mobile renderer for Quest, PC renderer for Link |
| XR runtime | OpenXR + XR Plug-in Management | 1.18.0 / 4.7.0; Meta Quest feature group on Android |
| Interaction | XR Interaction Toolkit | 3.6.1; joystick move, teleport, snap turn, ray UI |
| Input | Input System | 1.20.0; our own `BDCityControls` action map, rebindable in-game |
| City data | Rotterdam Digital Twins package + Cesium for Unity | Cesium 1.26.0; 3D Tiles streamed, then frozen into the project |
| Map data | PDOK (BGT base map, aerial photos) | Used by our editor tools |
| Style reference | X-CITE assets | Flat ground colours and illustrated trees |
| Characters | MakeHuman 1.2, Blender | Humanoid skeleton; walk animated in code |
| Fashion website | Static HTML + three.js 0.160 | Clickable mock-up with a 3D avatar preview |

Hardware: Meta Quest 3 (standalone APK; Quest Link over USB-C for testing) and an HP Reverb G2, which confirmed the OpenXR setup works beyond Meta.

## Project layout

```
Assets/
  BubbleFeature/      gift bubbles, gifts, bots, menu, audio (BubbleTest scene)
  CharacterFeature/   avatars, wardrobe, MakeHuman import tools
  CityFeature/        city scene, frozen neighbourhoods, tutorial, demo tools, mini game
  XR/, XRI/           OpenXR settings and XR Interaction Toolkit samples
DemoTools/            .bat scripts + scrcpy/adb (install APK, projector view)
fashion-site/         the website mock-up (open via a local web server)
```

The build contains one scene, `Assets/CityFeature/Scenes/CityScene.unity`. Most of the city's look is generated by re-runnable editor tools under the **B&D City** menu (freezing the streamed city, stylised buildings and facades, flat ground map, water, bridges, landmarks, trees and street props).

## Running it

- **Editor**: open `CityScene` and press Play, with a Quest connected over Quest Link (Link set as the active OpenXR runtime). Without a headset, use the XR Device Simulator and the mouse.
- **Quest APK**: build for Android (IL2CPP, ARM64), then run `DemoTools/Install B&D City on Quest.bat` with the Quest plugged in. It appears under Library → Unknown Sources as **B&D City**.
- **Projector view**: with the APK running and the Quest plugged in, run `DemoTools/Spectator view (projector).bat`; afterwards run `Restore Quest sleep.bat`.

## How we ran the demo

A classmate gave the pitch while one of us played in the headset and showed the features, with the headset view on a projector. Visitors with time and interest could then try the headset themselves. We had two headsets ready, so one could charge while the other was in use. On the Quest 3 the APK held a steady 72 fps, fully offline.

## Issues we ran into

| Area | Problem | Fix |
| --- | --- | --- |
| Network | The city streams live, and the venue Wi-Fi was unreliable | Froze each neighbourhood into the project; the demo runs offline |
| VR | Menu visible in one eye only over Link | Multi-pass rendering on Windows/Link |
| VR | Left menu button (≡) is reserved by Quest Link | Y opens the menu |
| VR | "Step Offset" error after the first teleport | Teleporter restores a minimum capsule height |
| VR | Pops blocked by trees and lamp posts | Aim assist plus a short grace period |
| City data | No bridge decks in the source data | Generated from the BGT map |
| City data | Blaak square looked like a crater field | Cleaned up the plaza terrain and metro shaft |
| City data | No NavMesh for bots on streamed ground | Raycast-based walking that turns at walls and quay edges |
| Look | City looked flat and "fake" | 3D facades, shopfronts, flat map ground, illustrated trees, street props |
| Performance | Far ground was ~250k triangles of raw tiles | Merged into a few unlit chunks; buildings and props merged per chunk |
| Characters | MakeHuman text-FBX skeleton imported flat | Automatic file repair in the avatar import tool |
| Characters | Skin and hair poking through clothes and hats | Baked body cover; hair hidden under hats |
| Demo | Raw headset mirror was lens-warped | In-app spectator camera streamed over USB |
| Demo | Unity froze when two adb copies fought over the headset | One tool at a time; Meta Quest Developer Hub closed |

## Not in this demo

Mirror buildings, floating mannequins, real clothing gifts, a connected fashion website, multiplayer with real users, designer uploads, and hand tracking were part of the concept but out of scope for the hackathon.

Note: `fashion-site/3d/obj+BASE+BALL+CAP.obj` (201 MB) is not in the repository because it exceeds GitHub's 100 MB file limit.
