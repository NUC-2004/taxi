using UnityEngine;
using UnityEngine.SceneManagement;

public static class Level0DialogueBootstrap
{
    public const string Level0SceneName = "Levle0";

    public static void StartLevel0()
    {
        Scene activeScene = SceneManager.GetActiveScene();
        if (activeScene.IsValid() && activeScene.name == Level0SceneName)
        {
            if (Object.FindFirstObjectByType<DialogueSequenceManager>() == null)
            {
                CreateLevel0Runtime();
            }
            return;
        }

        SceneManager.LoadScene(Level0SceneName);
    }

    public static void CreateLevel0Runtime()
    {
        if (Object.FindFirstObjectByType<DialogueSequenceManager>() != null)
        {
            return;
        }

        GameObject root = new GameObject("Level0 Dialogue Runtime");
        DialogueSequenceManager manager = root.AddComponent<DialogueSequenceManager>();
        DialogueUILayer ui = root.AddComponent<DialogueUILayer>();
        TutorialStepController tutorial = root.AddComponent<TutorialStepController>();

        ui.Initialize(manager);
        tutorial.Initialize(manager, ui);
    }
}
