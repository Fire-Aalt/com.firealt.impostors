# Benchmark sample

A small impostor scene with tools for scaling, measuring and building benchmarks.

## Contents

- `Content/Primitive Benchmark.unity`: two primitive props displayed as 100 placements.
  Each prop has two mesh LODs and a baked impostor with 4 × 4 views at 64 pixels per view and self-shadowing.
- `Content/Impostor Lit.shadergraph`: a Universal Lit graph with the Impostor Capture node.
  The prop materials use it for both mesh LODs and capture.
- `Content/Benchmark Pipeline.asset`: a Forward+ URP pipeline with two cascades, a 100 unit shadow distance,
  a 2048 main-light shadow map and Medium soft shadows.
  The sample never assigns it. Assign it yourself if you want to use it.

Use a project with SRP Batcher enabled and BatchRendererGroup Variants set to Keep All.

## Scaling the scene

Select the `ImpostorBenchmark` component or open **Tools > Impostors > Benchmark Scaling**.
Choose the grid dimensions and the culled percentage, then click **Apply**.
Placements are generated deterministically in memory, and Apply supports Undo.
The prefabs' `LODGroup` controls the mesh-to-impostor transitions. Move the camera to inspect them.

To bake a fresh copy from primitives, choose **Tools > Impostors > Samples > Create Editable Primitive Example**.
It creates the example in `Assets/Samples/Impostors/Portable` and never overwrites existing assets.

## Measuring

Entering Play does not start a measurement or write files.
Set the duration and run count on the live `ImpostorBenchmark` component, then call:

```csharp
var benchmark = UnityEngine.Object.FindFirstObjectByType<Impostors.Samples.Benchmark.ImpostorBenchmark>();
benchmark.BeginCapture(System.IO.Path.GetFullPath("Temp/ImpostorCapture"));
```

`StopCapture()` cancels. Disabling the component or exiting Play also stops it.
Captures do not change resolution, VSync, frame limits or pipeline settings.

Reports include frame, CPU and GPU timings, submitted instances, draw counters, worker timings, allocations,
hardware and settings, and screenshots.
Worker timings are summed CPU time across threads, not elapsed time.
A missing counter means it was unavailable, not that it cost nothing.

## Building

Save the sample scene under `Assets`, select the object with the `ImpostorBenchmark` component,
then choose **Tools > Impostors > Samples > Build Selected Benchmark for Windows DX12**.
Pick an empty output folder outside `Assets` and `Packages`.
The build temporarily uses the sample pipeline, DX12, frame timing and BRG variant retention,
then restores your previous settings.
It builds the whole saved scene, so use a clean scene to measure the renderer in isolation.

## Editor API

`ImpostorDelivery` exposes the same operations to scripts:

- `BakeDefinitions(prefabs, settings)` bakes the given source prefabs.
- `CreateBenchmark(prefabs, outputFolder, dimensionX, dimensionY)` saves a scene from baked prefabs.
- `GetBenchmarkPipeline(outputFolder)` creates or loads the sample pipeline assets without assigning them.
- `BuildBenchmark(benchmark, outputDirectory, development)` builds the selected saved scene.

Import the sample once per project, so its `Impostors.Samples.Benchmark.*` assemblies are not duplicated.
