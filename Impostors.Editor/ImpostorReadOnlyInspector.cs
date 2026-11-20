using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Impostors.Editor
{
    /// <summary>Draws every visible serialized property disabled, while keeping foldouts usable.</summary>
    internal static class ImpostorReadOnlyInspector
    {
        private const string SCRIPT_PROPERTY = "m_Script";

        public static void Draw(SerializedObject serialized, params string[] excluded)
        {
            serialized.UpdateIfRequiredOrScript();
            var names = new HashSet<string>(excluded) { SCRIPT_PROPERTY };
            SerializedProperty property = serialized.GetIterator();
            bool enter = true;
            while (property.NextVisible(enter))
            {
                enter = false;
                if (!names.Contains(property.name))
                {
                    DrawProperty(property);
                }
            }
        }

        private static void DrawProperty(SerializedProperty property)
        {
            if (!IsFoldout(property))
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.PropertyField(property, true);
                }

                return;
            }

            // Folding a generated list is a view operation. Its contents remain disabled.
            var label = new GUIContent(property.displayName, property.tooltip);
            property.isExpanded = EditorGUILayout.Foldout(property.isExpanded, label, true);
            if (!property.isExpanded)
            {
                return;
            }

            EditorGUI.indentLevel++;
            try
            {
                DrawChildren(property);
            }
            finally
            {
                EditorGUI.indentLevel--;
            }
        }

        private static void DrawChildren(SerializedProperty property)
        {
            SerializedProperty child = property.Copy();
            SerializedProperty end = property.GetEndProperty();
            if (!child.NextVisible(true))
            {
                return;
            }

            do
            {
                if (SerializedProperty.EqualContents(child, end))
                {
                    return;
                }

                DrawProperty(child);
            }
            while (child.NextVisible(false));
        }

        private static bool IsFoldout(SerializedProperty property)
        {
            return property.hasVisibleChildren && (property.isArray || property.propertyType
                is SerializedPropertyType.Generic or SerializedPropertyType.ManagedReference or SerializedPropertyType.Bounds);
        }
    }
}
