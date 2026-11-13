using UnityEditor;
using UnityEngine;
using UdonSharpEditor;

[CustomEditor(typeof(DungeonGenVRCComponent))]
public class DungeonGenVRCComponentEditor : Editor
{
    public override void OnInspectorGUI()
    {
        if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target)) return;
        serializedObject.Update();
        EditorGUI.BeginChangeCheck();
        DrawDefaultInspector();
        if (EditorGUI.EndChangeCheck())
        {
            serializedObject.ApplyModifiedProperties();
            ((DungeonGenVRCComponent)target).ApplyProxyModifications();
        }

        EditorGUILayout.Space(10f);
        EditorGUILayout.HelpBox(
            "Dungeon Map: Unity preview only. Dungeon: pre-bake several layouts, " +
            "then switch between them from an in-world Udon panel.",
            MessageType.Info);

        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
        {
            if (GUILayout.Button("Generate Dungeon (Editor)", GUILayout.Height(36)))
            {
                DungeonGenVRCComponent component = (DungeonGenVRCComponent)target;
                DungeonGenVRCWorldBaker.BakeFromComponent(component);
            }
        }
        if (GUILayout.Button("Save Scene"))
            UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
    }
}
