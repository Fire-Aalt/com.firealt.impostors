using System.Collections.Generic;
using Unity.Entities;
using UnityEditor;
using UnityEngine;

namespace EntityPainter.Editor
{
    /// <summary>
    /// One mouse-down to mouse-up paint or erase stroke. Dabs only change previews; <see cref="Commit"/> writes the
    /// target once, so live baking rebakes once per stroke and the stroke is one Undo step.
    /// </summary>
    internal sealed class EntityPainterStroke
    {
        private const string PAINT_UNDO = "Paint Entities";
        private const string ERASE_UNDO = "Erase Entities";
        private const float MIN_SCALE = 0.001f;

        private readonly PaintedEntities _target;
        private readonly EntityBrush _brush;
        private readonly EntityPainterPreview _preview;
        private readonly PaintedSpatialGrid _spacing;
        private readonly int _undoGroup;

        // Paint: target prototype indices of the enabled brush prefabs, and the records added per prototype.
        private readonly List<int> _paintPrototypes = new();
        private readonly Dictionary<int, List<PaintedRecord>> _additions = new();

        // Erase: target prototype indices the brush may erase, and the removed record flags.
        private readonly HashSet<int> _erasePrototypes = new();
        private readonly List<int> _dabHits = new();
        private readonly bool[] _removed;
        private int _removedCount;

        private Entity _spawner;

        /// <param name="spacing">The target's instances, for the brush's minimum distance. Null skips the check.</param>
        public EntityPainterStroke(PaintedEntities target, EntityBrush brush, EntityPainterPreview preview, bool erase,
            PaintedSpatialGrid spacing)
        {
            _target = target;
            _brush = brush;
            _preview = preview;
            _spacing = erase ? null : spacing;
            IsErase = erase;

            // A commit still pending from the previous stroke would otherwise clear this stroke's previews.
            _preview.Clear();

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(erase ? ERASE_UNDO : PAINT_UNDO);
            _undoGroup = Undo.GetCurrentGroup();

            if (erase)
            {
                _removed = new bool[target.InstanceCount];
                foreach (EntityBrushPrefab slot in brush.Prefabs)
                {
                    int index = slot.Enabled && slot.Prefab ? System.Array.IndexOf(target.Prototypes, slot.Prefab) : -1;
                    if (index >= 0)
                    {
                        _erasePrototypes.Add(index);
                    }
                }
            }
            else
            {
                CollectPaintPrototypes();
            }

            _preview.TryGetSpawner(target, out _spawner);
        }

        public bool IsErase { get; }

        /// <summary>True when the brush has nothing to place or erase.</summary>
        public bool IsEmpty => IsErase ? _erasePrototypes.Count == 0 : _paintPrototypes.Count == 0;

        public void Dab(Vector3 point, Vector3 normal)
        {
            // A prototype added by this stroke appears in the Editor world only after live baking catches up.
            if (_spawner == Entity.Null)
            {
                _preview.TryGetSpawner(_target, out _spawner);
            }

            if (IsErase)
            {
                Erase(point);
            }
            else
            {
                Paint(point, normal);
            }

            EditorApplication.QueuePlayerLoopUpdate();
        }

        /// <summary>Writes the stroke to the target. Returns false when the stroke changed nothing.</summary>
        public bool Commit()
        {
            bool changed = IsErase ? _removedCount > 0 : _additions.Count > 0;
            if (!changed)
            {
                Cancel();
                return false;
            }

            Undo.RecordObject(_target, IsErase ? ERASE_UNDO : PAINT_UNDO);
            if (IsErase)
            {
                PaintedRecordsEditing.Remove(_target, _removed, _removedCount);
            }
            else
            {
                PaintedRecordsEditing.Append(_target, _additions);
                // The grid already holds this stroke's points, so it now matches the committed records.
                _spacing?.Bind(_target, _target.Records);
            }

            EditorUtility.SetDirty(_target);
            Undo.CollapseUndoOperations(_undoGroup);
            _preview.ClearAfterRespawn(_spawner);
            EditorApplication.QueuePlayerLoopUpdate();
            return true;
        }

        /// <summary>Drops the previews. Prototypes registered at the stroke start stay, with no instances.</summary>
        public void Cancel()
        {
            _spacing?.Invalidate();
            _preview.Clear();
            Undo.CollapseUndoOperations(_undoGroup);
        }

        private void CollectPaintPrototypes()
        {
            bool recorded = false;
            foreach (EntityBrushPrefab slot in _brush.Prefabs)
            {
                if (!slot.Enabled || !slot.Prefab)
                {
                    continue;
                }

                if (System.Array.IndexOf(_target.Prototypes, slot.Prefab) < 0 && !recorded)
                {
                    // New prototypes are registered at once, so their prefabs bake and preview during the stroke.
                    Undo.RecordObject(_target, PAINT_UNDO);
                    recorded = true;
                }

                _paintPrototypes.Add(PaintedRecordsEditing.EnsurePrototype(_target, slot.Prefab));
            }

            if (recorded)
            {
                EditorUtility.SetDirty(_target);
            }
        }

        private void Paint(Vector3 point, Vector3 normal)
        {
            float radius = _brush.Size * 0.5f;
            int count = Mathf.Max(1, _brush.PrefabsPerDab);
            Vector3 tangent = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.99f ? Vector3.up : Vector3.right).normalized;
            Vector3 bitangent = Vector3.Cross(normal, tangent);

            for (int i = 0; i < count; i++)
            {
                Vector3 position = point;
                Vector3 surfaceNormal = normal;
                if (count > 1)
                {
                    Vector2 offset = Random.insideUnitCircle * radius;
                    Vector3 scattered = point + tangent * offset.x + bitangent * offset.y;
                    if (!EntityPainterSurface.TryProject(scattered, normal, radius, _brush.SurfaceLayers, out position,
                            out surfaceNormal))
                    {
                        continue;
                    }
                }

                if (_spacing != null)
                {
                    if (_spacing.HasNeighbour(position, _brush.MinDistance))
                    {
                        continue;
                    }

                    _spacing.Add(position);
                }

                int prototype = _paintPrototypes[Random.Range(0, _paintPrototypes.Count)];
                PaintedRecord record = PaintedRecord.Encode(position, PickRotation(surfaceNormal), PickScale());
                if (!_additions.TryGetValue(prototype, out List<PaintedRecord> records))
                {
                    records = new List<PaintedRecord>();
                    _additions.Add(prototype, records);
                }

                records.Add(record);
                _preview.Spawn(_spawner, prototype, record);
            }
        }

        private void Erase(Vector3 point)
        {
            float radius = _brush.Size * 0.5f;
            float radiusSquared = radius * radius;
            System.ReadOnlySpan<PaintedRecord> records = PaintedRecordsEditing.AsRecords(_target.Records);

            _dabHits.Clear();
            int start = 0;
            for (int prototype = 0; prototype < _target.Counts.Length; prototype++)
            {
                int end = start + _target.Counts[prototype];
                if (_erasePrototypes.Contains(prototype))
                {
                    for (int i = start; i < end; i++)
                    {
                        if (!_removed[i] && ((Vector3)records[i].Position - point).sqrMagnitude <= radiusSquared)
                        {
                            _removed[i] = true;
                            _removedCount++;
                            _dabHits.Add(i);
                        }
                    }
                }

                start = end;
            }

            _preview.Hide(_spawner, _target, _dabHits);
        }

        private Quaternion PickRotation(Vector3 surfaceNormal)
        {
            Vector3 min = _brush.MinRotation;
            Vector3 max = _brush.MaxRotation;
            Quaternion rotation = Quaternion.Euler(Random.Range(min.x, max.x), Random.Range(min.y, max.y),
                Random.Range(min.z, max.z));
            return _brush.AlignToNormal ? Quaternion.FromToRotation(Vector3.up, surfaceNormal) * rotation : rotation;
        }

        private float PickScale()
        {
            return Mathf.Max(MIN_SCALE, Random.Range(_brush.MinScale, _brush.MaxScale));
        }
    }
}
