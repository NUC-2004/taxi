using UnityEngine;

public sealed class TutorialStepController : MonoBehaviour
{
    [SerializeField] private DialogueSequenceManager dialogueManager;
    [SerializeField] private DialogueUILayer uiLayer;

    private bool hasSeenResponseWindow;
    private bool hasExplainedNoResponse;

    public void Initialize(DialogueSequenceManager manager, DialogueUILayer ui)
    {
        dialogueManager = manager;
        uiLayer = ui;
    }

    private void OnEnable()
    {
        TrySubscribe();
    }

    private void Start()
    {
        TrySubscribe();
    }

    private void OnDisable()
    {
        if (dialogueManager == null)
        {
            return;
        }

        dialogueManager.PhaseChanged -= HandlePhaseChanged;
        dialogueManager.ResponseResolved -= HandleResponseResolved;
        dialogueManager.SequenceCompleted -= HandleSequenceCompleted;
    }

    private void TrySubscribe()
    {
        if (dialogueManager == null)
        {
            dialogueManager = GetComponent<DialogueSequenceManager>();
        }

        if (uiLayer == null)
        {
            uiLayer = GetComponent<DialogueUILayer>();
        }

        if (dialogueManager == null)
        {
            return;
        }

        dialogueManager.PhaseChanged -= HandlePhaseChanged;
        dialogueManager.ResponseResolved -= HandleResponseResolved;
        dialogueManager.SequenceCompleted -= HandleSequenceCompleted;

        dialogueManager.PhaseChanged += HandlePhaseChanged;
        dialogueManager.ResponseResolved += HandleResponseResolved;
        dialogueManager.SequenceCompleted += HandleSequenceCompleted;
    }

    private void HandlePhaseChanged(DialoguePhase phase)
    {
        if (uiLayer == null)
        {
            return;
        }

        if (phase == DialoguePhase.NpcSpeaking && !hasSeenResponseWindow)
        {
            uiLayer.SetTutorialText("NPC is speaking. Responses are locked until this block ends.");
        }
        else if (phase == DialoguePhase.PlayerResponse)
        {
            hasSeenResponseWindow = true;
            uiLayer.SetTutorialText("Your turn. Use Up/Down to choose, Space to confirm before the bar closes.");
        }
    }

    private void HandleResponseResolved(ResponseResult result)
    {
        if (uiLayer == null)
        {
            return;
        }

        if (!result.WasChosen)
        {
            hasExplainedNoResponse = true;
        uiLayer.SetTutorialText("No response lowers engagement. The conversation keeps moving.");
        }
        else if (!hasExplainedNoResponse)
        {
            uiLayer.SetTutorialText("Good. A chosen response resolves the window immediately.");
        }
        else
        {
            uiLayer.SetTutorialText("The next NPC block starts automatically.");
        }
    }

    private void HandleSequenceCompleted()
    {
        if (uiLayer != null)
        {
            uiLayer.SetTutorialText("Level 0 loop complete.");
        }
    }
}
