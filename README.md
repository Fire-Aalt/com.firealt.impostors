# Impostors

A prefab impostor baker and an ECS-owned BatchRendererGroup (BRG) renderer for Unity URP.
Mesh LODs transition into depth impostors, so very large populations of trees or props render in a handful of draws.
Entities Graphics is not required.

> [!WARNING]
> This package is experimental.
> APIs, serialized data and bake formats may change between releases.

## Requirements

Version 0.1.0-exp.1 targets Unity **6000.6.7f1**, URP/Shader Graph **17.6.0**, and Entities/Collections **6.6.0**.
The manifest declares the required packages.

- Use a URP **Forward+** pipeline with **SRP Batcher** enabled.
- Set **Project Settings > Graphics > Shader Stripping > BatchRendererGroup Variants** to **Keep All**.
  The renderer does not use Entities Graphics, which would otherwise retain those variants.
- Add **Impostors Renderer Feature** to the URP renderer to use baked self-shadowing.
- Baking requires texture arrays and BC7 compression.
  Optional self-shadow baking also requires compute shaders, asynchronous GPU readback and BC4-compressed 3D textures.
- Rendering requires compute shaders, indirect draw arguments and raw graphics buffers.
  GLES and WebGL are not supported.
  Creating the renderer on such a device throws an error naming what is missing.
- Mesh LODs must use triangle topology. A mesh imported with **Keep Quads** fails with an error naming the mesh.

Windows is the only validated platform so far.
Importing the package never changes graphics settings, and opening a scene or entering Play never bakes assets.

## Installation

In **Package Manager**, choose **+ > Install package from git URL** and enter:

```
https://github.com/Fire-Aalt/com.firealt.impostors.git
```

Append `#<tag>` to pin a release.
You can also add this folder as an embedded package or a local package through its `package.json`.

## Quick start

1. Prepare a source prefab in writable project `Assets`.
   Give it a `LODGroup` with static mesh renderers.
   Its materials must use a capture-ready shader, such as `Impostors/ImpostorLit` or `Impostors/ImpostorFoliage`.
   See [Included shaders](#included-shaders), or prepare your own graph as described in
   [Shader Graph setup](#shader-graph-setup).
2. Create **Assets > Create > Impostors > Bake Settings**.
3. Select the prefab's **Impostor** component, or open **Tools > Impostors > Baker**, choose the bake settings and bake.
   All source mesh LODs stay mesh levels. The impostor occupies the final Culled range of the `LODGroup`.
4. Place the prefab in a SubScene, reference it with `GetEntity()` from a baker,
   or spawn it at runtime through `ImpostorRenderingSystem`.

A bake embeds the definition, texture arrays, proxy mesh, materials and optional self-shadow volume
into the source prefab.
Rebakes keep the definition identity. A cancelled or failed bake keeps the previous completed bake.
Changing the source graph, textures or capture settings requires an explicit rebake.

### Included shaders

The package includes two capture-ready Shader Graphs.
Select them in a material's shader menu, or copy one into `Assets` as a starting point for your own graph.

| Shader                      | File                                            | Use                                     |
| --------------------------- | ----------------------------------------------- | --------------------------------------- |
| `Impostors/ImpostorLit`     | `Impostors/Shaders/ImpostorLit.shadergraph`     | Opaque surfaces, in place of URP Lit    |
| `Impostors/ImpostorFoliage` | `Impostors/Shaders/ImpostorFoliage.shadergraph` | Two-sided leaves, needles and grass     |

#### ImpostorLit

ImpostorLit uses the metallic workflow and the same property names as URP Lit.
Switching a URP Lit material to ImpostorLit keeps its textures, with two differences:

- **Metallic** multiplies the metallic map. Set it to 1 on materials that use a map.
- Emission is always on. Set **Emission Color** to black on materials that had emission disabled.

#### ImpostorFoliage

ImpostorFoliage is two-sided and alpha-clipped.
Back faces mirror the normal, so both sides of a leaf are lit.

| Property group | Properties                                                                    |
| -------------- | ----------------------------------------------------------------------------- |
| Surface        | Base Map, Base Color, Alpha Clip Threshold, Normal Map, Normal Scale          |
| Mask           | Mask Map (G: occlusion, A: smoothness), Smoothness, Occlusion Strength        |
| Subsurface     | Subsurface Tint, Subsurface Strength, Subsurface Spread, Subsurface Ambient   |
| Wind           | Wind Direction, Wind Strength, Wind Speed, Wind Flutter                       |

Wind bends the mesh along **Wind Direction** in proportion to height above the pivot.
Subsurface adds sun transmission when looking towards the sun.
**Wind Strength** and **Subsurface Strength** are 0 by default.

Capture renders the rest pose without wind and transmission, because both depend on time or lighting.
Meshes sway while impostors stay still.
To match transmission, enable **Advanced › Subsurface scattering** on the baked impostor material
and enter the same Tint, Strength, Spread and Ambient values.

### Shader Graph setup

1. In an opaque or cutout **Universal Lit** graph, add **Impostors > Impostor Capture**.
2. Route Base Color, Normal, Metallic or Specular, Smoothness, Occlusion, Emission, Alpha and Alpha Clip Threshold
   through the node's matching ports to the Master Stack.
   Keep the world position, normal, tangent and bitangent inputs at their World defaults.
3. Add a **Boolean Keyword** with reference `_ImpostorCaptureEnabled`, Definition **Shader Feature**, Scope **Local**
   and default **Off**. Connect it to **Capture Enabled**.
4. Set **Normal Space** to 0 (Tangent), 1 (Object) or 2 (World), matching the graph.
   Set **Specular Workflow** to match the graph's Lit workflow.
5. Enable **Supports LOD Cross Fade** if the `LODGroup` uses fade bands.

With the keyword off, the node passes values through unchanged.
Stock URP Lit works for mesh LODs but cannot be a capture source. Use `Impostors/ImpostorLit` instead.
Transparent, custom-lighting and clear-coat surfaces are not supported.

### Capture settings

| Setting              | Effect                                                                                                   |
| -------------------- | -------------------------------------------------------------------------------------------------------- |
| **Angle matrix**     | Number of captured views, for example 8 × 8 or 16 × 16.                                                  |
| **Final resolution** | Atlas-equivalent pixel budget, up to 4096 × 4096. Each view gets a power-of-two size of at least 32 px.  |
| **Source LOD**       | The mesh LOD used as capture geometry. All LODs still render as meshes at runtime.                       |
| **Supersampling**    | Capture resolution multiplier before downsampling. Costs bake time, not final memory.                    |
| **Border**           | Padding in final pixels around each view, so filtering does not bleed between views.                     |
| **Self Shadowing**   | Bakes an optional per-prototype visibility volume for distant sunlight shadows.                          |

New settings default to 12 × 12 views at 256 × 256 pixels with an 8 pixel border and 2× supersampling.
The inspector shows the estimated texture memory.

### Spawning at runtime

```csharp
var system = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<ImpostorRenderingSystem>();
Entity prefab = system.CreatePrefab(sourcePrefab);
using var transforms = new NativeArray<float4x4>(count, Allocator.Temp);
// Fill transforms. Only positive uniform scale is supported.
NativeArray<Entity> instances = system.Instantiate(prefab, transforms, Allocator.Persistent);
```

`CreatePrefab` returns the same single-entity prefab that SubScene baking produces.
`Instantiate` creates a whole batch and writes the transforms in a Burst job.
`EntityManager.Instantiate` and command buffers work too.
Move instances by writing `LocalToWorld`, and destroy them with `EntityManager.DestroyEntity`.

### Material controls

Each baked prefab has its own impostor material. All controls are neutral by default and need no rebake.

| Group                 | Controls                                                                                   |
| --------------------- | ------------------------------------------------------------------------------------------ |
| Surface               | Base Color tint and gain, albedo contrast, saturation, lift, per-instance hue variation.   |
| Reflectance           | Specular tint and strength, direct highlights, reflection probes.                          |
| Material detail       | Smoothness scale and offset, normal strength, metallic influence.                          |
| Captured AO           | Strength, contrast and offset.                                                             |
| Emission              | Emission tint and strength.                                                                |
| Advanced › Distance   | Fades parallax and view blending by projected size.                                        |
| Advanced › Subsurface | Optional sun-transmission lobe that replaces subsurface lighting the capture omits.        |
| Advanced › Shadows    | Realtime receiver bias, and strength, normal bias and response of baked self-shadows.      |

Material edits reach the scene on the next Editor update, including in Play mode and after Undo.
Player code can call `RefreshMaterialProperties()`.

## How it works

### Baking

The baker renders the chosen source LOD from a hemi-octahedral grid of view directions
over the object's upper hemisphere.
It clones the source material, enables `_ImpostorCaptureEnabled`, and draws the original shader's ForwardLit pass.
The capture node then writes raw surface data instead of lit colour.
No replacement shader is generated, copied or compiled.

Each view becomes a slice of a texture array rather than a cell of one atlas.
This prevents bleeding between neighbouring views. The arrays are:

- Base Color and coverage in an sRGB BC7 array.
- Octahedral normal, AO and smoothness in a linear BC7 array.
- Reflectance (F0 and diffuse weight) in a BC7 array, or one material constant when every texel matches.
- Depth in a BC4 or R16 array, used to reconstruct the surface position.
- Optional HDR emission and optional geometric-normal/metallic data for the material controls.

With **Self Shadowing** enabled, a compute shader also bakes an object-space visibility volume.
It stores geometric occlusion for many sun directions, not lighting from one fixed sun.
Rotating the object around its vertical axis therefore needs no rebake.

### Rendering

Every placement is an ECS entity with only `LocalToWorld`, bounds, LOD metrics and a GPU slot.
`ImpostorRenderingSystem` runs in each Default and Editor world.
It finds entities with a baked prototype and creates one renderer per layer and shadow setting.
Renderers are disposed with the world.

Instances are grouped into chunks by prototype and 128 m cell.
Chunk bounds let culling reject distant groups before testing individual instances.
A presentation system uploads only changed transforms, so unchanged populations upload nothing.

Burst culling jobs select a mesh LOD or the impostor per instance and per view, including LOD crossfades.
Camera and shadow views draw with indirect BRG commands.
Direct BRG draws split every 256 instances, but indirect draws read the visible list from a buffer of any length.
The visible lists reach the GPU through a copy of Entities Graphics' `SparseUploader`.

The impostor shader blends the nearest captured views, reconstructs depth with parallax,
and lights the result with URP's `UniversalFragmentPBR`.
Impostors therefore receive Forward+ additional lights, cookies, reflection probes, fog and realtime shadows
like the meshes.
Mixed lighting, shadowmasks, APV and lightmaps are not supported.

### Shadows

Inside the realtime shadow distance, impostors cast and receive regular URP shadows.
Towards the end of that distance, sunlight visibility fades from the realtime shadow map to the baked visibility volume.
Distant impostors keep self-shadowing without shadow-map cost.
Only direct sunlight uses baked visibility. Ambient, reflections, emission and other lights are unaffected.

For a smooth transition, finish the mesh-to-impostor fade before realtime shadows fade out.
If impostors look darker than the meshes because they shadow themselves, raise **Realtime receiver bias**.

## Entity Painter

Open **Tools > Entity Painter** to paint any baked prefab as entities
into a `PaintedEntities` component inside an open SubScene.
Brushes are reusable `EntityBrush` assets with a prefab palette, surface layers, size, spacing, count per dab,
and rotation and scale ranges.
Each stroke is one Undo step. `PaintedEntitySpawnSystem` spawns the instances in Play mode and in the Editor world.
The `EntityPainter*` assemblies do not depend on Impostors.

## Samples

Import the **Benchmark** sample from the package's **Samples** tab in Package Manager.
Unity copies it to `Assets/Samples/Impostors/<version>/Benchmark`, where its scenes, prefabs and settings are editable.
It contains two primitive props with baked impostors in a 100-placement scene, a Forward+ pipeline,
and tools for scaling, measuring and building benchmarks.
See the [Benchmark sample instructions](Samples~/Benchmark/README.md).

## Development

Each assembly's root namespace equals its name:

| Assembly                    | Folder                       | Contents                                                       |
| --------------------------- | ---------------------------- | -------------------------------------------------------------- |
| `Impostors.Data`            | `Impostors.Data/`            | ECS components, definition and LOD assets, shader constants    |
| `Impostors`                 | `Impostors/`                 | Systems, the BRG world renderer and its jobs, renderer feature |
| `Impostors.Authoring`       | `Impostors.Authoring/`       | Bakers and baking systems (`UNITY_EDITOR` only)                |
| `Impostors.Editor`          | `Impostors.Editor/`          | Capture baker, inspectors, windows                             |
| `Impostors.Tests`           | `Impostors.Tests/`           | NUnit tests (`UNITY_INCLUDE_TESTS`)                            |
| `Impostors.Tests.Authoring` | `Impostors.Tests/Authoring/` | Test-only authoring components with nested bakers              |
| `EntityPainter*`            | `EntityPainter*/`            | Entity Painter runtime, authoring, editor and tests            |

Add `com.firealt.impostors` to the project's `testables` list to run the package tests.
