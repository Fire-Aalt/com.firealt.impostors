using Impostors.Data;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
namespace Impostors.Tests
{
 public class ImpostorEntityTests
 {
  [Test]
  public void PrototypeBlobRefreshPreservesIdentityAndUpdatesAllBindings()
  {
   using var renderer = new ImpostorWorldRenderer();
   var prefab = ImpostorTestAssets.Prefab(1);
   var definition = ImpostorTestAssets.Definition(1);
   var originalBounds = definition.Bounds;
   try
   {
    var em = renderer.EntityWorld.EntityManager;
    var template = renderer.BakeEntityPrefab(prefab);
    var first = em.Instantiate(template);
    var second = em.Instantiate(template);
    renderer.SynchronizeEntities();
    var blob = em.GetSharedComponent<ImpostorPrototype>(template).Data;
    Assert.That(blob.Value.SourcePrefabName.ToString(), Is.EqualTo(prefab.name));
    Assert.That(em.GetSharedComponent<ImpostorPrototype>(first).Data, Is.EqualTo(blob));
    Assert.That(em.GetSharedComponent<ImpostorCleanupPrototype>(second).Data, Is.EqualTo(blob));
    var oldExtents = em.GetComponentData<RenderBounds>(first).Extents;
    definition.Bounds = new Bounds(originalBounds.center, originalBounds.size * 4);
    renderer.RefreshPrototypeBindings();
    Assert.That(em.GetSharedComponent<ImpostorPrototype>(template).Data, Is.EqualTo(blob));
    Assert.That(em.GetSharedComponent<ImpostorPrototype>(second).Data.Value.Bounds.Extents.x,
     Is.GreaterThan(oldExtents.x));
    Assert.That(em.GetComponentData<RenderBounds>(first).Extents.x, Is.GreaterThan(oldExtents.x));
    Assert.That(em.GetComponentData<RenderBounds>(second).Extents.x, Is.GreaterThan(oldExtents.x));
    em.DestroyEntity(second);
    renderer.SynchronizeEntities();
    Assert.That(em.Exists(second), Is.False);
   }
   finally { definition.Bounds = originalBounds; }
  }
 }
}
