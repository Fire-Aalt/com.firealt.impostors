using UnityEditor;
using UnityEngine;

namespace EntityPainter.Editor
{
    /// <summary>Shows the painted instance counts without drawing the packed record array.</summary>
    [CustomEditor(typeof(PaintedEntities))]
    internal sealed class PaintedEntitiesEditor : UnityEditor.Editor
    {
        private const string OPEN_PAINTER = "Open Entity Painter";
        private const string TOTAL_FORMAT = "{0:N0} instances, {1:N0} KB";
        private const string MISSING_PROTOTYPE = "Missing prototype";
        private const float BYTES_PER_KB = 1024f;

        public override void OnInspectorGUI()
        {
            var painted = (PaintedEntities)target;
            DrawCounts(painted);
            if (GUILayout.Button(OPEN_PAINTER))
            {
                EntityPainterWindow.Open(painted);
            }
        }

        /// <summary>Draws each prototype with its instance count, and the total.</summary>
        public static void DrawCounts(PaintedEntities target)
        {
            int prototypes = Mathf.Min(target.Prototypes.Length, target.Counts.Length);
            using (new EditorGUI.DisabledScope(true))
            {
                for (int i = 0; i < prototypes; i++)
                {
                    GameObject prototype = target.Prototypes[i];
                    string label = target.Counts[i].ToString("N0");
                    if (prototype)
                    {
                        EditorGUILayout.ObjectField(label, prototype, typeof(GameObject), false);
                    }
                    else
                    {
                        EditorGUILayout.LabelField(label, MISSING_PROTOTYPE);
                    }
                }
            }

            EditorGUILayout.LabelField(string.Format(TOTAL_FORMAT, target.InstanceCount,
                Mathf.CeilToInt(target.Records.Length / BYTES_PER_KB)), EditorStyles.miniLabel);
        }
    }
}
