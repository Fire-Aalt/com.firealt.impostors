using Impostors.Data;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
namespace Impostors.Tests {
public class ImpostorEntityMutationTests {
 [Test] public void SharedWorldUsesPrototypeRegistrationsForDiscoveryAndCleanup() {
  using var world = new World("Prototype registration fixture");
  using var a = new ImpostorWorldRenderer(world); using var b = new ImpostorWorldRenderer(world);
  var prefab = ImpostorTestAssets.Prefab(1);
  var first = world.EntityManager.Instantiate(a.BakeEntityPrefab(prefab));
  var second = world.EntityManager.Instantiate(b.BakeEntityPrefab(prefab));
  world.EntityManager.SetComponentData(second, new LocalToWorld { Value = float4x4.Translate(new float3(500, 0, 0)) });
  a.SynchronizeEntities(); b.SynchronizeEntities();
  Assert.That(a.CaptureRenderData().Instances[0].Position, Is.EqualTo(Vector3.zero));
  Assert.That(b.CaptureRenderData().Instances[0].Position, Is.EqualTo(new Vector3(500, 0, 0)));
  world.EntityManager.SetComponentData(second, new LocalToWorld { Value = float4x4.Translate(new float3(700, 0, 0)) });
  a.SynchronizeEntities(); Assert.That(a.UploadedBytesLastSync, Is.Zero);
  b.SynchronizeEntities(); Assert.That(b.UploadedBytesLastSync, Is.EqualTo(104));
  world.EntityManager.DestroyEntity(second);
  a.SynchronizeEntities();
  Assert.That(world.EntityManager.Exists(second), Is.True, "Another renderer must leave this GPU cleanup pending.");
  b.SynchronizeEntities();
  Assert.That(world.EntityManager.Exists(second), Is.False);
  Assert.That(a.InstanceCount, Is.EqualTo(1)); Assert.That(b.InstanceCount, Is.Zero);
  Assert.That(world.EntityManager.Exists(first), Is.True);
 }

 [Test] public void MultiChunkRegroupAndDestructionKeepAllEntities() {
  using var renderer = new ImpostorWorldRenderer(); var em = renderer.EntityWorld.EntityManager;
  var template = renderer.BakeEntityPrefab(ImpostorTestAssets.Prefab(1));
  using var entities = new NativeArray<Entity>(513, Allocator.Temp);
  em.Instantiate(template, entities);
  for (int i = 0; i < entities.Length; i++)
   em.SetComponentData(entities[i], new LocalToWorld { Value = float4x4.Translate(new float3((i % 5) * 130, 0, (i / 5) * 2)) });
  renderer.SynchronizeEntities();
  for (int i = 0; i < entities.Length; i++)
   em.SetComponentData(entities[i], new LocalToWorld { Value = float4x4.Translate(new float3(-260 + i % 2, 0, 390)) });
  renderer.SynchronizeEntities();
  Assert.That(renderer.InstanceCount, Is.EqualTo(513));
  foreach (var entity in entities)
   Assert.That(em.GetSharedComponent<ImpostorSpatialCell>(entity).Value, Is.EqualTo(new int2(-3, 3)).Or.EqualTo(new int2(-2, 3)));
  for (int i = 0; i < entities.Length; i += 2) em.DestroyEntity(entities[i]);
  renderer.SynchronizeEntities();
  Assert.That(renderer.InstanceCount, Is.EqualTo(256));
  renderer.SynchronizeEntities();
  Assert.That(renderer.UploadedBytesLastSync, Is.Zero);
 }

}}
