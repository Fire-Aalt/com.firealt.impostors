# Changelog

All notable changes to this package are documented in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the package uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Impostors Renderer Feature › Occluded Depth Rejection**, on by default. When URP's prepass renders straight into
  the camera depth texture, impostor depth and normals are drawn by the feature right after it, and quads hidden
  behind opaque geometry are skipped before reconstruction. Impostors write per-pixel depth, and HLSLcc drops
  `SV_DepthLessEqual` on Vulkan, so hardware early-Z never rejected hidden impostor fragments there.
- **Impostors Renderer Feature › Spatial Cell Size** replaces the fixed 128 m chunk cells. Changing it regroups
  every instance at runtime, including entities baked with another size.
- **Impostors Renderer Feature › Hull Detail** picks the plane set of the hull proxies: Low (about 44 triangles),
  Medium (84, default) or High (300). Medium measured fastest; High's extra triangles cost more than the few percent
  of pixels they save.
- Experimental **Depth Bands**: the impostor depth pass draws half-octave distance buckets near to far, each skipping
  quads hidden behind the nearer ones. Off (1) by default; each band adds one depth copy.
- Experimental **Occlusion Culling**: culls instances hidden behind a coarse farthest-depth map of the Game camera,
  read back asynchronously. Off by default; trees revealed by fast camera motion can appear one to three frames late.

### Changed

- Impostors are drawn as a convex hull fitted to all of the prototype's mesh LOD vertices, with planes concentrated
  near the horizon, instead of a camera-facing bounding rectangle. On conifers it covers 55% of the rectangle's
  pixels, which cuts impostor depth, shadow and shading overdraw. When no mesh LOD can be read, the baked 14-DOP is
  drawn instead.
- `ImpostorProxy.BuildCameraQuad`, `CAMERA_QUAD_VERTEX_COUNT` and `CAMERA_QUAD_TRIANGLE_COUNT` are removed, and
  `ConfigureCameraProxy` is renamed `ConfigureProxyVolume`. `ImpostorProxy.BuildPrototypeHull` builds a prototype's
  drawn proxy. Benchmark reports carry `HullProxy` in place of `CameraFacingProxy`.

- Camera depth buckets span half an octave of distance instead of a full octave, and each bucket is its own draw range.

### Fixed

- The impostor shader no longer keeps per-view state in local arrays. They compiled to indexable temps, which
  Vulkan drivers placed in scratch memory, making every impostor fragment roughly 15 times more expensive in all passes.
- **Impostors Offscreen Shade** skips quads hidden behind the scene depth instead of shading every impostor behind walls.
- Forward impostor shading, native or offscreen, skips quads behind the prepass depth and discards reconstructed
  surfaces that lie behind it before lighting, so overlapping impostors no longer pay for PBR, self-shadowing and
  the additional-light loop of every hidden layer.

- **Mesh LOD Generator** no longer copies the source model root's position into the generated LODs.
  A model exported away from its origin, such as an FBX whose object sat off-centre in Blender, placed its LODs
  and impostor far from the prefab pivot, so painted or spawned instances appeared far from where they were placed.
  Existing generated prefabs show as outdated; regenerate them, then rebake the impostor.

## [0.1.0-exp.1] - 2026-11-20

First experimental release.
APIs, serialized data and bake formats may change in later releases.

### Added

- Prefab impostor baking from the **Impostor** inspector or **Tools > Impostors > Baker**.
  Bakes embed the definition, texture arrays, proxy, materials and an optional self-shadow volume
  into the source prefab.
  Capture uses a node-enabled Universal Lit graph with a local static `_ImpostorCaptureEnabled` keyword.
  Mesh LOD materials keep their original shaders.
- **Mesh LOD Generator**, which builds a prefab's mesh LODs and `LODGroup` from one model
  with Unity's Mesh LOD simplifier. Set the LOD count and the reduction per LOD, with optional per-LOD overrides.
  **Create > Impostors > Impostor Prefab from Model** sets up a new prefab from a model in one step.
- Capture-ready `Impostors/ImpostorLit` and `Impostors/ImpostorFoliage` Shader Graphs.
  ImpostorFoliage adds two-sided normals, alpha clipping, wind and sun transmission.
- ECS-owned BatchRendererGroup rendering through `ImpostorRenderingSystem`, without Entities Graphics.
  `Impostor` prefabs bake natively from a SubScene or `GetEntity()`.
  They spawn at runtime through `CreatePrefab` and batched `Instantiate`.
- Indirect camera and shadow draws, nearest-first view order, and screen-size based detail reduction.
- Forward+ lighting with a realtime-to-baked sunlight handoff, realtime receiver bias,
  and optional subsurface scattering (`_IMPOSTOR_SUBSURFACE`).
- **Impostors Renderer Feature** for prototype self-shadow lighting.
- **Entity Painter** (**Tools > Entity Painter**), which paints any baked prefab as entities
  into a `PaintedEntities` component inside an open SubScene.
  It uses separate `EntityPainter*` assemblies that do not depend on Impostors.
- **Benchmark** sample with two primitive props, a 100-placement scene, a Forward+ URP pipeline,
  and scaling, measurement and build tools.
  Import it from the Package Manager **Samples** tab.

[0.1.0-exp.1]: https://github.com/Fire-Aalt/com.firealt.impostors/tree/main
