# Native animation port

Approved scope: keep a standalone WPF demo; use #5a4ac8, enlarged capsule
eyes, autonomous gaze, and no WebView2 or Node runtime dependency. Integrate
with the island only after the user accepts the demo.

Reference: [ArvinYin1/grokbot-animation-anatomy](https://github.com/ArvinYin1/grokbot-animation-anatomy),
commit debd41625da89b4c4fde43b3b1f0add6dc113c02. The repository README declares
MIT; its recipe file is extracted animation data and the README says the
underlying data remains owned by its original owners and is published for study.
This demo embeds only the 28 clip recipes in grok-clips.json for local testing;
review data rights before redistributing the demo.

The native runtime loads all 28 source clip IDs, preserving their position,
rotation, squash, scale, eyelid and expression keyframes with per-key Bézier
easing. The tray exposes every clip under its source family; the three source
idle clips are not used for autonomous idle, which retains the demo's existing
random gaze behavior. Source expression aliases are mapped onto the demo's eye
poses, and blink events use the source timing. The two non-sphere body families
still render as the requested purple sphere, and the 53-state registry and its
procedural morph shapes are not included in this port.

The fixed-step spring, state bridge, eye geometry, body color, and autonomous
idle behavior remain native demo code. This is a focused animation-data port,
not a recreation of the original renderer or complete state machine.
