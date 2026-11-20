using System.Collections.Generic;
using Unity.Scenes;
using Unity.Scenes.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EntityPainter.Editor
{
    /// <summary>
    /// Paints prefabs as entities into a <see cref="PaintedEntities"/> target inside an open SubScene.
    /// The brush asset holds the palette and settings; the window only remembers which brush and target are active.
    /// </summary>
    internal sealed class EntityPainterWindow : EditorWindow
    {
        private const string TITLE = "Entity Painter";
        private const string MENU_PATH = "Tools/Entity Painter";
        private const string BRUSH_LABEL = "Brush";
        private const string NEW_BRUSH = "New";
        private const string NEW_BRUSH_MESSAGE = "Choose where to save the brush.";
        private const string BRUSH_EXTENSION = "asset";
        private const string TARGET_LABEL = "Target";
        private const string CREATE_TARGET = "Create";
        private const string TARGET_NAME = "Painted Entities";
        private const string CREATE_TARGET_UNDO = "Create Painted Entities";
        private const string NO_BRUSH = "Assign or create a brush.";
        private const string NO_TARGET = "Assign a Painted Entities target, or create one in an open SubScene.";
        private const string TARGET_NOT_IN_SUBSCENE =
            "The target must be inside a SubScene that is open for editing, so baking can save its instances.";
        private const string NO_OPEN_SUBSCENE = "Open a SubScene for editing first.";
        private const string PLAYING = "Painting is disabled in Play mode.";
        private const string INCONSISTENT_TARGET = "The target's data is inconsistent. Clear it to repair it.";
        private const string AUTHORING_VIEW =
            "The Scene view shows authoring data, which hides Entities Graphics instances in open SubScenes. "
            + "Set Entities > Scene View Mode to Runtime Data.";
        private const string SHOW_RUNTIME = "Open Entities Preferences";
        private const string ENTITIES_PREFERENCES = "Preferences/Entities";
        private const string PALETTE_HEADER = "Palette";
        private const string DROP_PREFABS = "Drop prefabs here";
        private const string REMOVE_SLOT = "X";
        private const string PLACEMENT_HEADER = "Placement";
        private const string ROTATION_HEADER = "Rotation";
        private const string SCALE_HEADER = "Scale";
        private const string INSTANCES_HEADER = "Instances";
        private const string CLEAR_ALL = "Clear All";
        private const string CLEAR_UNDO = "Clear Painted Entities";
        private const string CLEAR_TITLE = "Clear Painted Entities";
        private const string CLEAR_MESSAGE = "Remove all {0:N0} instances from '{1}'? This can be undone.";
        private const string CLEAR_CONFIRM = "Clear";
        private const string CANCEL = "Cancel";
        private const string HINTS =
            "Left drag: paint or erase. Shift: erase while painting. Ctrl + scroll: brush size. Esc: stop.";
        private const string SIZE_UNDO = "Change Brush Size";
        private const string NO_SURFACE = "No surface on the brush layers under the cursor";

        private const string PREFABS_PROPERTY = nameof(EntityBrush.Prefabs);
        private const string PREFAB_PROPERTY = nameof(EntityBrushPrefab.Prefab);
        private const string ENABLED_PROPERTY = nameof(EntityBrushPrefab.Enabled);
        private const string LAYERS_PROPERTY = nameof(EntityBrush.SurfaceLayers);
        private const string SIZE_PROPERTY = nameof(EntityBrush.Size);
        private const string DELTA_PROPERTY = nameof(EntityBrush.DeltaDistance);
        private const string PER_DAB_PROPERTY = nameof(EntityBrush.PrefabsPerDab);
        private const string MIN_DISTANCE_PROPERTY = nameof(EntityBrush.MinDistance);
        private const string ALIGN_PROPERTY = nameof(EntityBrush.AlignToNormal);
        private const string MIN_ROTATION_PROPERTY = nameof(EntityBrush.MinRotation);
        private const string MAX_ROTATION_PROPERTY = nameof(EntityBrush.MaxRotation);
        private const string MIN_SCALE_PROPERTY = nameof(EntityBrush.MinScale);
        private const string MAX_SCALE_PROPERTY = nameof(EntityBrush.MaxScale);

        private const float MAX_SIZE = 500f;
        private const float MAX_DELTA = 200f;
        private const int MAX_PER_DAB = 200;
        private const float CELL_SIZE = 72f;
        private const float CELL_SPACING = 4f;
        private const float TOGGLE_SIZE = 16f;
        private const float DROP_HEIGHT = 36f;
        private const float SCROLL_SIZE_STEP = 0.05f;
        private const float NORMAL_TICK = 0.25f;

        private static readonly string[] ModeLabels = { "Off", "Paint", "Erase" };
        private static readonly Color PaintColor = new(0.3f, 1f, 0.3f, 1f);
        private static readonly Color EraseColor = new(1f, 0.3f, 0.3f, 1f);
        private static readonly Vector2 HintOffset = new(16f, 16f);

        [SerializeField] private EntityBrush _brush;
        [SerializeField] private PaintedEntities _target;
        [SerializeField] private EntityPainterMode _mode;

        private readonly EntityPainterPreview _preview = new();
        private readonly int _controlHint = TITLE.GetHashCode();
        private EntityPainterStroke _stroke;
        private SerializedObject _brushObject;
        private PaintedSpatialGrid _spacing;
        private Vector3 _lastDab;
        private Vector2 _scroll;

        [MenuItem(MENU_PATH)]
        private static void Open()
        {
            GetWindow<EntityPainterWindow>(TITLE);
        }

        /// <summary>Opens the window with <paramref name="target"/> selected.</summary>
        public static void Open(PaintedEntities target)
        {
            EntityPainterWindow window = GetWindow<EntityPainterWindow>(TITLE);
            window._target = target;
            window.Repaint();
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += OnEditorUpdate;
            Undo.undoRedoPerformed += Repaint;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.update -= OnEditorUpdate;
            Undo.undoRedoPerformed -= Repaint;
            CancelStroke();
            _preview.Clear();
        }

        private void OnEditorUpdate()
        {
            _preview.Update();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawBrushField();
            DrawTargetField();

            if (_brush)
            {
                if (_brushObject == null || _brushObject.targetObject != _brush)
                {
                    _brushObject = new SerializedObject(_brush);
                }

                _brushObject.Update();
                DrawModeToolbar();
                DrawPalette(_brushObject.FindProperty(PREFABS_PROPERTY));
                DrawSettings(_brushObject);
                _brushObject.ApplyModifiedProperties();
            }
            else
            {
                EditorGUILayout.HelpBox(NO_BRUSH, MessageType.Info);
            }

            DrawInstances();
            EditorGUILayout.HelpBox(HINTS, MessageType.None);
            EditorGUILayout.EndScrollView();
        }

        private void DrawBrushField()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                _brush = (EntityBrush)EditorGUILayout.ObjectField(BRUSH_LABEL, _brush, typeof(EntityBrush), false);
                if (GUILayout.Button(NEW_BRUSH, GUILayout.Width(60f)))
                {
                    CreateBrush();
                }
            }
        }

        private void DrawTargetField()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var target = (PaintedEntities)EditorGUILayout.ObjectField(TARGET_LABEL, _target, typeof(PaintedEntities), true);
                if (target != _target)
                {
                    CancelStroke();
                    _target = target;
                }

                if (GUILayout.Button(CREATE_TARGET, GUILayout.Width(60f)))
                {
                    CreateTarget();
                }
            }

            if (!TryGetBlocker(out string blocker))
            {
                if (LiveConversionEditorSettings.LiveConversionMode == LiveConversionMode.SceneViewShowsAuthoring)
                {
                    EditorGUILayout.HelpBox(AUTHORING_VIEW, MessageType.Warning);
                    if (GUILayout.Button(SHOW_RUNTIME))
                    {
                        SettingsService.OpenUserPreferences(ENTITIES_PREFERENCES);
                    }
                }

                return;
            }

            EditorGUILayout.HelpBox(blocker, MessageType.Warning);
        }

        private void DrawModeToolbar()
        {
            EditorGUILayout.Space();
            var mode = (EntityPainterMode)GUILayout.Toolbar((int)_mode, ModeLabels);
            if (mode != _mode)
            {
                CancelStroke();
                _mode = mode;
                SceneView.RepaintAll();
            }
        }

        private void DrawPalette(SerializedProperty prefabs)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(PALETTE_HEADER, EditorStyles.boldLabel);

            float width = EditorGUIUtility.currentViewWidth - 24f;
            int columns = Mathf.Max(1, Mathf.FloorToInt((width + CELL_SPACING) / (CELL_SIZE + CELL_SPACING)));
            int rows = Mathf.CeilToInt(prefabs.arraySize / (float)columns);
            Rect grid = GUILayoutUtility.GetRect(width, rows * (CELL_SIZE + CELL_SPACING));

            int remove = -1;
            for (int i = 0; i < prefabs.arraySize; i++)
            {
                var cell = new Rect(grid.x + i % columns * (CELL_SIZE + CELL_SPACING),
                    grid.y + i / columns * (CELL_SIZE + CELL_SPACING), CELL_SIZE, CELL_SIZE);
                if (DrawSlot(cell, prefabs.GetArrayElementAtIndex(i)))
                {
                    remove = i;
                }
            }

            if (remove >= 0)
            {
                prefabs.DeleteArrayElementAtIndex(remove);
            }

            DrawDropArea(prefabs);
        }

        /// <summary>Draws one palette slot. Returns true when its remove button was clicked.</summary>
        private bool DrawSlot(Rect cell, SerializedProperty slot)
        {
            SerializedProperty prefab = slot.FindPropertyRelative(PREFAB_PROPERTY);
            SerializedProperty enabled = slot.FindPropertyRelative(ENABLED_PROPERTY);
            Object asset = prefab.objectReferenceValue;

            GUI.Box(cell, GUIContent.none);
            Texture preview = asset ? AssetPreview.GetAssetPreview(asset) : null;
            if (!preview && asset)
            {
                preview = AssetPreview.GetMiniThumbnail(asset);
                if (AssetPreview.IsLoadingAssetPreviews())
                {
                    Repaint();
                }
            }

            if (preview)
            {
                Color color = GUI.color;
                GUI.color = enabled.boolValue ? color : new Color(color.r, color.g, color.b, 0.35f);
                GUI.DrawTexture(cell, preview, ScaleMode.ScaleToFit);
                GUI.color = color;
            }

            var toggle = new Rect(cell.x + 2f, cell.y + 2f, TOGGLE_SIZE, TOGGLE_SIZE);
            enabled.boolValue = GUI.Toggle(toggle, enabled.boolValue, GUIContent.none);

            var tooltip = new GUIContent(string.Empty, asset ? asset.name : string.Empty);
            GUI.Label(cell, tooltip);

            var removeRect = new Rect(cell.xMax - TOGGLE_SIZE - 2f, cell.y + 2f, TOGGLE_SIZE, TOGGLE_SIZE);
            return GUI.Button(removeRect, REMOVE_SLOT, EditorStyles.miniButton);
        }

        private static void DrawDropArea(SerializedProperty prefabs)
        {
            Rect area = GUILayoutUtility.GetRect(0f, DROP_HEIGHT, GUILayout.ExpandWidth(true));
            GUI.Box(area, DROP_PREFABS, EditorStyles.helpBox);

            Event current = Event.current;
            if (current.type is not (EventType.DragUpdated or EventType.DragPerform) || !area.Contains(current.mousePosition))
            {
                return;
            }

            var dropped = new List<GameObject>();
            foreach (Object reference in DragAndDrop.objectReferences)
            {
                if (reference is GameObject gameObject && PrefabUtility.IsPartOfPrefabAsset(gameObject))
                {
                    dropped.Add(gameObject.transform.root.gameObject);
                }
            }

            DragAndDrop.visualMode = dropped.Count > 0 ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (current.type == EventType.DragPerform && dropped.Count > 0)
            {
                DragAndDrop.AcceptDrag();
                foreach (GameObject prefab in dropped)
                {
                    if (!ContainsPrefab(prefabs, prefab))
                    {
                        int index = prefabs.arraySize++;
                        SerializedProperty slot = prefabs.GetArrayElementAtIndex(index);
                        slot.FindPropertyRelative(PREFAB_PROPERTY).objectReferenceValue = prefab;
                        slot.FindPropertyRelative(ENABLED_PROPERTY).boolValue = true;
                    }
                }
            }

            current.Use();
        }

        private static bool ContainsPrefab(SerializedProperty prefabs, GameObject prefab)
        {
            for (int i = 0; i < prefabs.arraySize; i++)
            {
                if (prefabs.GetArrayElementAtIndex(i).FindPropertyRelative(PREFAB_PROPERTY).objectReferenceValue == prefab)
                {
                    return true;
                }
            }

            return false;
        }

        private static void DrawSettings(SerializedObject brush)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(PLACEMENT_HEADER, EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(brush.FindProperty(LAYERS_PROPERTY));
            EditorGUILayout.Slider(brush.FindProperty(SIZE_PROPERTY), 0.1f, MAX_SIZE);
            EditorGUILayout.Slider(brush.FindProperty(DELTA_PROPERTY), 0f, MAX_DELTA);
            EditorGUILayout.IntSlider(brush.FindProperty(PER_DAB_PROPERTY), 1, MAX_PER_DAB);
            EditorGUILayout.Slider(brush.FindProperty(MIN_DISTANCE_PROPERTY), 0f, MAX_DELTA);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(ROTATION_HEADER, EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(brush.FindProperty(ALIGN_PROPERTY));
            EditorGUILayout.PropertyField(brush.FindProperty(MIN_ROTATION_PROPERTY));
            EditorGUILayout.PropertyField(brush.FindProperty(MAX_ROTATION_PROPERTY));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(SCALE_HEADER, EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(brush.FindProperty(MIN_SCALE_PROPERTY));
            EditorGUILayout.PropertyField(brush.FindProperty(MAX_SCALE_PROPERTY));
        }

        private void DrawInstances()
        {
            if (!_target)
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(INSTANCES_HEADER, EditorStyles.boldLabel);
            PaintedEntitiesEditor.DrawCounts(_target);

            if (!_target.IsConsistent)
            {
                EditorGUILayout.HelpBox(INCONSISTENT_TARGET, MessageType.Error);
            }

            using (new EditorGUI.DisabledScope(_target.InstanceCount == 0 && _target.IsConsistent))
            {
                if (GUILayout.Button(CLEAR_ALL) && EditorUtility.DisplayDialog(CLEAR_TITLE,
                        string.Format(CLEAR_MESSAGE, _target.InstanceCount, _target.name), CLEAR_CONFIRM, CANCEL))
                {
                    CancelStroke();
                    Undo.RecordObject(_target, CLEAR_UNDO);
                    PaintedRecordsEditing.Clear(_target);
                    _target.FormatVersion = PaintedEntities.FORMAT_VERSION;
                    EditorUtility.SetDirty(_target);
                    EditorApplication.QueuePlayerLoopUpdate();
                }
            }
        }

        private void OnSceneGUI(SceneView view)
        {
            if (_mode == EntityPainterMode.Off || !_brush || TryGetBlocker(out _))
            {
                return;
            }

            Event current = Event.current;
            int controlId = GUIUtility.GetControlID(_controlHint, FocusType.Passive);
            EventType type = current.GetTypeForControl(controlId);
            if (type == EventType.Layout)
            {
                HandleUtility.AddDefaultControl(controlId);
                return;
            }

            if (type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
            {
                CancelStroke();
                _mode = EntityPainterMode.Off;
                Repaint();
                current.Use();
                return;
            }

            if (type == EventType.ScrollWheel && current.control)
            {
                Undo.RecordObject(_brush, SIZE_UNDO);
                _brush.Size = Mathf.Clamp(_brush.Size * (1f - current.delta.y * SCROLL_SIZE_STEP), 0.1f, MAX_SIZE);
                EditorUtility.SetDirty(_brush);
                Repaint();
                current.Use();
                return;
            }

            bool hit = EntityPainterSurface.TryPick(current.mousePosition, _brush.SurfaceLayers, out Vector3 point,
                out Vector3 normal);
            bool erase = _stroke?.IsErase ?? (_mode == EntityPainterMode.Erase || current.shift);

            switch (type)
            {
                case EventType.Repaint when hit:
                    DrawBrush(point, normal, erase);
                    break;
                case EventType.Repaint:
                    DrawCursorHint(current.mousePosition, NO_SURFACE);
                    break;
                case EventType.MouseMove:
                    view.Repaint();
                    break;
                case EventType.MouseDown when current.button == 0 && !current.alt && hit:
                    GUIUtility.hotControl = controlId;
                    _stroke = new EntityPainterStroke(_target, _brush, _preview, erase, GetSpacing(erase));
                    _stroke.Dab(point, normal);
                    _lastDab = point;
                    current.Use();
                    break;
                case EventType.MouseDrag when GUIUtility.hotControl == controlId && _stroke != null:
                    if (hit && Vector3.Distance(point, _lastDab) >= Mathf.Max(_brush.DeltaDistance, 0.01f))
                    {
                        _stroke.Dab(point, normal);
                        _lastDab = point;
                    }

                    view.Repaint();
                    current.Use();
                    break;
                case EventType.MouseUp when GUIUtility.hotControl == controlId:
                    GUIUtility.hotControl = 0;
                    _stroke?.Commit();
                    _stroke = null;
                    Repaint();
                    current.Use();
                    break;
            }
        }

        /// <summary>The cached spacing grid for paint strokes with a minimum distance, rebuilt only when the records changed.</summary>
        private PaintedSpatialGrid GetSpacing(bool erase)
        {
            if (erase || _brush.MinDistance <= 0f)
            {
                return null;
            }

            _spacing = PaintedSpatialGrid.GetOrBuild(_spacing, _target, _brush.MinDistance);
            return _spacing;
        }

        private void DrawBrush(Vector3 point, Vector3 normal, bool erase)
        {
            float radius = _brush.Size * 0.5f;
            Handles.color = erase ? EraseColor : PaintColor;
            Handles.DrawWireDisc(point, normal, radius);
            Handles.DrawLine(point, point + normal * radius * NORMAL_TICK);
        }

        private static void DrawCursorHint(Vector2 mousePosition, string message)
        {
            Handles.BeginGUI();
            var content = new GUIContent(message);
            Vector2 size = EditorStyles.helpBox.CalcSize(content);
            GUI.Label(new Rect(mousePosition + HintOffset, size), content, EditorStyles.helpBox);
            Handles.EndGUI();
        }

        /// <summary>Returns false when painting is possible, otherwise the reason it is not.</summary>
        private bool TryGetBlocker(out string blocker)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                blocker = PLAYING;
                return true;
            }

            if (!_target)
            {
                blocker = NO_TARGET;
                return true;
            }

            if (!_target.gameObject.scene.isSubScene)
            {
                blocker = TARGET_NOT_IN_SUBSCENE;
                return true;
            }

            if (!_target.IsConsistent)
            {
                blocker = INCONSISTENT_TARGET;
                return true;
            }

            blocker = null;
            return false;
        }

        private void CancelStroke()
        {
            _stroke?.Cancel();
            _stroke = null;
        }

        private void CreateBrush()
        {
            string path = EditorUtility.SaveFilePanelInProject(TITLE, EntityBrush.FILE_NAME, BRUSH_EXTENSION, NEW_BRUSH_MESSAGE);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var brush = CreateInstance<EntityBrush>();
            AssetDatabase.CreateAsset(brush, path);
            AssetDatabase.SaveAssets();
            _brush = brush;
        }

        private void CreateTarget()
        {
            var scenes = new List<Scene>();
            foreach (SubScene subScene in SubScene.AllSubScenes)
            {
                if (subScene && subScene.IsLoaded)
                {
                    scenes.Add(subScene.EditingScene);
                }
            }

            if (scenes.Count == 0)
            {
                EditorUtility.DisplayDialog(TITLE, NO_OPEN_SUBSCENE, CANCEL);
                return;
            }

            if (scenes.Count == 1)
            {
                CreateTarget(scenes[0]);
                return;
            }

            var menu = new GenericMenu();
            foreach (Scene scene in scenes)
            {
                menu.AddItem(new GUIContent(scene.name), false, () => CreateTarget(scene));
            }

            menu.ShowAsContext();
        }

        private void CreateTarget(Scene scene)
        {
            var gameObject = new GameObject(TARGET_NAME, typeof(PaintedEntities));
            SceneManager.MoveGameObjectToScene(gameObject, scene);
            Undo.RegisterCreatedObjectUndo(gameObject, CREATE_TARGET_UNDO);
            _target = gameObject.GetComponent<PaintedEntities>();
            Selection.activeGameObject = gameObject;
            Repaint();
        }
    }
}
