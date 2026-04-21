using System.Linq;
using UnityEditor;
using UnityEngine;

public static class MissingScriptTools
{
    [MenuItem("Tools/Cleanup/Remove Missing Scripts From Selection")]
    private static void RemoveMissingScriptsFromSelection()
    {
        GameObject[] selected = Selection.gameObjects;
        if (selected == null || selected.Length == 0)
        {
            Debug.Log("No GameObjects selected.");
            return;
        }

        int totalRemoved = 0;
        for (int i = 0; i < selected.Length; i++)
        {
            GameObject[] hierarchy = selected[i].GetComponentsInChildren<Transform>(true)
                .Select(t => t.gameObject)
                .ToArray();

            for (int j = 0; j < hierarchy.Length; j++)
            {
                totalRemoved += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(hierarchy[j]);
            }
        }

        Debug.Log("Removed missing scripts: " + totalRemoved);
    }
}
