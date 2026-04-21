using UnityEngine;
using UnityEngine.SceneManagement;

public static class MainMenuBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateMainMenu()
    {
        if (SceneManager.GetActiveScene().name == Level0DialogueBootstrap.Level0SceneName)
        {
            return;
        }

        if (Object.FindFirstObjectByType<MainMenuController>(FindObjectsInactive.Include) != null)
        {
            return;
        }

        GameObject root = new GameObject("Main Menu Runtime");
        root.AddComponent<MainMenuController>();
    }

    public static void ShowMainMenu()
    {
        MainMenuController existingMenu = Object.FindFirstObjectByType<MainMenuController>(FindObjectsInactive.Include);
        if (existingMenu != null)
        {
            existingMenu.gameObject.SetActive(true);
            return;
        }

        GameObject root = new GameObject("Main Menu Runtime");
        root.AddComponent<MainMenuController>();
    }
}
