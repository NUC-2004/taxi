using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class DialogueSequenceManager : MonoBehaviour
{
    private const string ConversationFailureMessage = "(Seems the passenger does not want to talk anymore.)";

    [Header("Sequence")]
    public float secondsPerNpcFragment = 1.7f;
    public float extraNpcFragmentHoldSeconds = 0.5f;
    public float typewriterCharacterInterval = 0.035f;
    public float responseWindowSeconds = 10f;
    public float betweenBlocksDelaySeconds = 0.45f;
    public int defaultNoResponseAffectionDelta = -12;

    [Header("Affection")]
    public int initialAffection = 5;
    public int minAffection = 0;
    public int maxAffection = 10;

    private readonly List<NpcSpeakingBlock> blocks = new List<NpcSpeakingBlock>();
    private readonly Dictionary<string, NpcSpeakingBlock> blockLookup = new Dictionary<string, NpcSpeakingBlock>();
    private Coroutine sequenceRoutine;
    private bool waitingForResponse;
    private int currentBlockIndex = -1;
    private int affection;
    private string pendingNextBlockId;
    private NpcSpeakingBlock activeBlock;
    private string loadedSequenceId = "Level0";
    private bool sequenceFailed;
    private bool waitingForNpcAdvance;
    private bool npcAdvanceRequested;
    private bool typewriterSkipRequested;
    private bool isTypewriterPlaying;
    private bool npcAdvanceLocked;

    public event Action<DialoguePhase> PhaseChanged;
    public event Action<NpcSpeakingBlock, int, int> BlockChanged;
    public event Action<NpcSpeakingBlock, int, int> NpcFragmentPresented;
    public event Action<string> NpcTextChanged;
    public event Action<IReadOnlyList<PlayerResponseOption>> ResponseStarted;
    public event Action<float, float> ResponseTimerChanged;
    public event Action<int, int, int> AffectionChanged;
    public event Action<ResponseResult> ResponseResolved;
    public event Action SequenceFailed;
    public event Action SequenceCompleted;

    public DialoguePhase CurrentPhase { get; private set; } = DialoguePhase.None;
    public int Affection => affection;
    public int MinAffection => minAffection;
    public int MaxAffection => maxAffection;
    public float ResponseWindowSeconds => responseWindowSeconds;
    public float CurrentResponseDurationSeconds { get; private set; }
    public string FailureMessage { get; private set; } = ConversationFailureMessage;
    public string ActiveBlockId => activeBlock != null ? activeBlock.blockId : string.Empty;

    private void Awake()
    {
        EnsureTrafficSignalPrompt();
        EnsureDialogueMusicGate();
    }

    private void Start()
    {
        if (blocks.Count == 0)
        {
            LoadDialogueDataForActiveScene();
        }

        Begin();
    }

    public void SetSequence(IEnumerable<NpcSpeakingBlock> sequence)
    {
        blocks.Clear();
        blocks.AddRange(sequence);
        BuildBlockLookup();
    }

    public void Begin()
    {
        if (sequenceRoutine != null)
        {
            StopCoroutine(sequenceRoutine);
        }

        activeBlock = null;
        sequenceFailed = false;
        ResetNpcAdvanceState();
        FailureMessage = ConversationFailureMessage;
        CurrentResponseDurationSeconds = 0f;
        affection = Mathf.Clamp(initialAffection, minAffection, maxAffection);
        AffectionChanged?.Invoke(affection, minAffection, maxAffection);
        BuildBlockLookup();
        EnsureDrivingMinigame();
        sequenceRoutine = StartCoroutine(RunSequence());
    }

    public void FailSequence(string message)
    {
        if (sequenceFailed || CurrentPhase == DialoguePhase.Complete)
        {
            return;
        }

        if (sequenceRoutine != null)
        {
            StopCoroutine(sequenceRoutine);
            sequenceRoutine = null;
        }

        TriggerSequenceFailure(string.IsNullOrWhiteSpace(message) ? ConversationFailureMessage : message);
    }

    public void ChangeAffection(int delta, string failureMessage = null)
    {
        if (sequenceFailed || CurrentPhase == DialoguePhase.Complete || delta == 0)
        {
            return;
        }

        ApplyAffectionDelta(delta, failureMessage);
    }

    public void RequestNpcAdvance()
    {
        if (CurrentPhase != DialoguePhase.NpcSpeaking || sequenceFailed)
        {
            return;
        }

        if (isTypewriterPlaying)
        {
            typewriterSkipRequested = true;
            return;
        }

        if (waitingForNpcAdvance && !npcAdvanceLocked)
        {
            npcAdvanceRequested = true;
        }
    }

    public void SetNpcAdvanceLocked(bool locked)
    {
        npcAdvanceLocked = locked;
    }

    public void ForceNpcAdvance()
    {
        if (CurrentPhase != DialoguePhase.NpcSpeaking || sequenceFailed)
        {
            return;
        }

        if (waitingForNpcAdvance)
        {
            npcAdvanceRequested = true;
        }
    }

    public void ChooseResponse(PlayerResponseOption option)
    {
        if (CurrentPhase != DialoguePhase.PlayerResponse || !waitingForResponse || option == null)
        {
            return;
        }

        if (IsOptionLockedForCurrentState(option))
        {
            return;
        }

        waitingForResponse = false;
        if (activeBlock != null)
        {
            StorySessionState.RecordChoice(activeBlock.blockId, option.optionId);
        }
        pendingNextBlockId = option.nextBlockId;
        ApplyAffectionDelta(option.affectionDelta);
        if (sequenceFailed)
        {
            return;
        }

        ResponseResolved?.Invoke(new ResponseResult(true, option, option.affectionDelta, pendingNextBlockId));
    }

    private IEnumerator RunSequence()
    {
        NpcSpeakingBlock block = blocks.Count > 0 ? blocks[0] : null;
        int safetyCount = 0;

        while (block != null && safetyCount < 200)
        {
            safetyCount++;
            activeBlock = block;
            currentBlockIndex = blocks.IndexOf(block);
            pendingNextBlockId = block.nextBlockId;
            BlockChanged?.Invoke(block, currentBlockIndex, blocks.Count);

            SetPhase(DialoguePhase.NpcSpeaking);
            yield return PlayNpcBlock(block);

            if (block.allowsPlayerResponse && GetAvailableResponseOptions(block).Count > 0)
            {
                yield return RunResponseWindow(block);
                if (sequenceFailed)
                {
                    yield break;
                }
            }

            yield return new WaitForSeconds(betweenBlocksDelaySeconds);
            if (sequenceFailed)
            {
                yield break;
            }

            block = ResolveNextBlock(block, pendingNextBlockId);
        }

        if (safetyCount >= 200)
        {
            Debug.LogWarning("Dialogue stopped after 200 blocks. Check for an accidental loop in Next Block data.");
        }

        SetPhase(DialoguePhase.Complete);
        if (loadedSequenceId == "Level0")
        {
            StorySessionState.SetTone(ResolveLevel0Tone());
        }
        SequenceCompleted?.Invoke();
    }

    private IEnumerator PlayNpcBlock(NpcSpeakingBlock block)
    {
        string[] fragments = block.npcTextFragments;
        if (fragments == null || fragments.Length == 0)
        {
            NpcTextChanged?.Invoke(string.Empty);
            yield break;
        }

        for (int i = 0; i < fragments.Length; i++)
        {
            yield return PlayTypewriterFragment(fragments[i]);
            NpcFragmentPresented?.Invoke(block, i, fragments.Length);
            yield return WaitForNpcAdvance();
        }
    }

    private IEnumerator PlayTypewriterFragment(string fragment)
    {
        typewriterSkipRequested = false;
        if (string.IsNullOrEmpty(fragment))
        {
            NpcTextChanged?.Invoke(string.Empty);
            yield break;
        }

        isTypewriterPlaying = true;
        for (int i = 1; i <= fragment.Length; i++)
        {
            NpcTextChanged?.Invoke(fragment.Substring(0, i));

            if (typewriterSkipRequested)
            {
                NpcTextChanged?.Invoke(fragment);
                break;
            }

            if (i < fragment.Length)
            {
                yield return new WaitForSeconds(typewriterCharacterInterval);
            }
        }

        isTypewriterPlaying = false;
        typewriterSkipRequested = false;
    }

    private IEnumerator WaitForNpcAdvance()
    {
        waitingForNpcAdvance = true;
        npcAdvanceRequested = false;

        while (!npcAdvanceRequested && !sequenceFailed && CurrentPhase == DialoguePhase.NpcSpeaking)
        {
            yield return null;
        }

        waitingForNpcAdvance = false;
        npcAdvanceRequested = false;
    }

    private IEnumerator RunResponseWindow(NpcSpeakingBlock block)
    {
        SetPhase(DialoguePhase.PlayerResponse);
        waitingForResponse = true;
        List<PlayerResponseOption> availableOptions = GetAvailableResponseOptions(block);
        float configuredDuration = block.responseWindowSecondsOverride >= 0f
            ? block.responseWindowSecondsOverride
            : responseWindowSeconds;
        bool useTimer = configuredDuration > 0f;
        CurrentResponseDurationSeconds = configuredDuration;
        ResponseStarted?.Invoke(availableOptions);

        float remaining = configuredDuration;
        while (waitingForResponse && (!useTimer || remaining > 0f))
        {
            ResponseTimerChanged?.Invoke(useTimer ? remaining : 0f, configuredDuration);
            if (useTimer)
            {
                remaining -= Time.deltaTime;
            }
            yield return null;
        }

        if (waitingForResponse)
        {
            waitingForResponse = false;
            int delta = block.noResponseAffectionDelta;
            if (delta == 0 && string.IsNullOrEmpty(block.noResponseNextBlockId))
            {
                delta = defaultNoResponseAffectionDelta;
            }

            StorySessionState.RecordNoResponse(block.blockId);
            pendingNextBlockId = block.noResponseNextBlockId;
            ApplyAffectionDelta(delta);
            if (sequenceFailed)
            {
                yield break;
            }

            ResponseResolved?.Invoke(new ResponseResult(false, null, delta, pendingNextBlockId));
        }

        CurrentResponseDurationSeconds = 0f;
        ResponseTimerChanged?.Invoke(0f, configuredDuration);
    }

    private string ResolveLevel0Tone()
    {
        if (StorySessionState.WasBlockMissed("L0_B4_RESPONSE"))
        {
            return "COLD";
        }

        int responseCount = 0;
        string[] trackedBlocks =
        {
            "L0_B1_RESPONSE",
            "L0_B2_RESPONSE",
            "L0_B3_RESPONSE",
            "L0_B4_RESPONSE"
        };

        for (int i = 0; i < trackedBlocks.Length; i++)
        {
            if (!StorySessionState.WasBlockMissed(trackedBlocks[i]))
            {
                responseCount++;
            }
        }

        if (responseCount >= 3)
        {
            return "WARM";
        }

        if (responseCount == 2)
        {
            return "NEUTRAL_WARM";
        }

        if (responseCount == 1)
        {
            return "NEUTRAL";
        }

        return "COLD";
    }

    private void ApplyAffectionDelta(int delta, string failureMessage = null)
    {
        affection = Mathf.Clamp(affection + delta, minAffection, maxAffection);
        AffectionChanged?.Invoke(affection, minAffection, maxAffection);

        if (delta < 0 &&
            !sequenceFailed &&
            IsFailureEnabledSequence() &&
            affection <= minAffection)
        {
            TriggerSequenceFailure(string.IsNullOrWhiteSpace(failureMessage) ? ConversationFailureMessage : failureMessage);
        }
    }

    private void TriggerSequenceFailure(string message)
    {
        sequenceFailed = true;
        waitingForResponse = false;
        ResetNpcAdvanceState();
        pendingNextBlockId = null;
        CurrentResponseDurationSeconds = 0f;
        FailureMessage = string.IsNullOrWhiteSpace(message) ? ConversationFailureMessage : message;
        ResponseTimerChanged?.Invoke(0f, 0f);
        NpcTextChanged?.Invoke(FailureMessage);
        SetPhase(DialoguePhase.Failed);
        SequenceFailed?.Invoke();
    }

    private bool IsFailureEnabledSequence()
    {
        return string.Equals(loadedSequenceId, "Level0", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(loadedSequenceId, "Level1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(loadedSequenceId, "Level2", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(loadedSequenceId, "Level3", StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureDrivingMinigame()
    {
        if (!IsDrivingMinigameScene(SceneManager.GetActiveScene().name) ||
            GetComponent<DrivingMinigameController>() != null)
        {
            return;
        }

        gameObject.AddComponent<DrivingMinigameController>();
    }

    private void EnsureTrafficSignalPrompt()
    {
        string sceneName = SceneManager.GetActiveScene().name;
        bool shouldUseTrafficSignalPrompt =
            string.Equals(sceneName, "Level2", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sceneName, "Level3", StringComparison.OrdinalIgnoreCase);

        if (!shouldUseTrafficSignalPrompt ||
            GetComponent<TrafficSignalPromptController>() != null)
        {
            return;
        }

        gameObject.AddComponent<TrafficSignalPromptController>();
    }

    private void EnsureDialogueMusicGate()
    {
        if (!string.Equals(SceneManager.GetActiveScene().name, "Level3", StringComparison.OrdinalIgnoreCase) ||
            GetComponent<DialogueMusicGateController>() != null)
        {
            return;
        }

        gameObject.AddComponent<DialogueMusicGateController>();
    }

    private static bool IsDrivingMinigameScene(string sceneName)
    {
        return string.Equals(sceneName, "Level0", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sceneName, "Level1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sceneName, "Level2", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sceneName, "Level3", StringComparison.OrdinalIgnoreCase);
    }

    private void SetPhase(DialoguePhase phase)
    {
        CurrentPhase = phase;
        PhaseChanged?.Invoke(phase);
    }

    private void ResetNpcAdvanceState()
    {
        waitingForNpcAdvance = false;
        npcAdvanceRequested = false;
        typewriterSkipRequested = false;
        isTypewriterPlaying = false;
        npcAdvanceLocked = false;
    }

    private void BuildBlockLookup()
    {
        blockLookup.Clear();
        for (int i = 0; i < blocks.Count; i++)
        {
            NpcSpeakingBlock block = blocks[i];
            if (block == null || string.IsNullOrWhiteSpace(block.blockId))
            {
                continue;
            }

            blockLookup[block.blockId] = block;
        }
    }

    private NpcSpeakingBlock ResolveNextBlock(NpcSpeakingBlock currentBlock, string nextBlockId)
    {
        if (string.Equals(nextBlockId, "END", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(nextBlockId))
        {
            if (blockLookup.TryGetValue(nextBlockId, out NpcSpeakingBlock nextBlock))
            {
                return nextBlock;
            }

            Debug.LogWarning("Dialogue next block not found: " + nextBlockId);
            return null;
        }

        int currentIndex = blocks.IndexOf(currentBlock);
        int nextIndex = currentIndex + 1;
        return nextIndex >= 0 && nextIndex < blocks.Count ? blocks[nextIndex] : null;
    }

    private static List<PlayerResponseOption> GetAvailableResponseOptions(NpcSpeakingBlock block)
    {
        List<PlayerResponseOption> availableOptions = new List<PlayerResponseOption>();
        if (block == null || block.responseOptions == null)
        {
            return availableOptions;
        }

        for (int i = 0; i < block.responseOptions.Count; i++)
        {
            PlayerResponseOption option = block.responseOptions[i];
            if (option == null)
            {
                continue;
            }

            availableOptions.Add(option);
        }

        return availableOptions;
    }

    public static bool IsOptionLocked(PlayerResponseOption option)
    {
        return option != null &&
               !string.IsNullOrWhiteSpace(option.requiredOptionId) &&
               !StorySessionState.HasSelectedOption(option.requiredOptionId);
    }

    public bool IsOptionLockedForCurrentState(PlayerResponseOption option)
    {
        return GetOptionLockReason(option) != null;
    }

    public string GetOptionLockReason(PlayerResponseOption option)
    {
        if (option == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(option.requiredOptionId) &&
            !StorySessionState.HasSelectedOption(option.requiredOptionId))
        {
            return "earlier choice required";
        }

        if (option.requiredAffection > 0 && affection < option.requiredAffection)
        {
            return "Engagement " + affection + "/" + option.requiredAffection;
        }

        return null;
    }

    private void LoadDialogueDataForActiveScene()
    {
        blocks.Clear();
        string sceneName = SceneManager.GetActiveScene().name;
        if (string.Equals(sceneName, "Level1", StringComparison.OrdinalIgnoreCase))
        {
            loadedSequenceId = "Level1";
            LoadLevel1KeywordData();
            return;
        }

        if (string.Equals(sceneName, "Level2", StringComparison.OrdinalIgnoreCase))
        {
            loadedSequenceId = "Level2";
            LoadLevel2MargaretData();
            return;
        }

        if (string.Equals(sceneName, "Level3", StringComparison.OrdinalIgnoreCase))
        {
            loadedSequenceId = "Level3";
            LoadLevel3OwenData();
            return;
        }

        loadedSequenceId = "Level0";
        LoadLevel0PlaceholderData();
    }

    private void LoadLevel0PlaceholderData()
    {
        blocks.Add(Block(
            "L0_B1_SETUP",
            new[]
            {
                N("Just past eleven at night."),
                N("The cab idles at the curb. The rear door opens."),
                N("A young girl gets in, closes it quietly, and sets her bag on the seat beside her."),
                N("She glances ahead and gives the address."),
                Q("Exhibition Center, please."),
                N("A pause. She takes out her phone, checks the screen, puts it away without scrolling."),
                N("The cab does not move."),
                Q("...You're not saying anything."),
                N("She leans forward slightly, looking toward the front."),
                Q("Oh -- I get it. You're the type that needs to pick something."),
                N("She tilts her chin in a vague direction."),
                Q("Up and down. Pick one. You don't need to think too hard.")
            },
            "L0_B1_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L0_B1_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L0_B1_A", "Sure. Let's go.", 0, "L0_B1_A_REPLY"),
                Option("L0_B1_B", "Exhibition center this late?", 1, "L0_B1_B_REPLY"),
                Option("L0_B1_C", "...", 0, "L0_B1_C_REPLY")
            },
            -1,
            "L0_B1_MISS",
            0f));

        blocks.Add(Block("L0_B1_A_REPLY", new[] { N("The cab pulls out. The girl settles back."), Q("Not bad. Quick.") }, "L0_B2_SETUP"));
        blocks.Add(Block("L0_B1_B_REPLY", new[] { Q("Friend has a show there, it's the last night."), N("Short. No elaboration. She turns back to the window.") }, "L0_B2_SETUP"));
        blocks.Add(Block("L0_B1_C_REPLY", new[] { Q("Silent driver. Noted."), N("Said softly, like a memo to herself. She almost smiles.") }, "L0_B2_SETUP"));
        blocks.Add(Block("L0_B1_MISS", new[] { N("No option selected. The cab moves anyway."), N("The girl looks ahead. She does not try again."), N("Her fingers close briefly around her bag strap.") }, "L0_B2_SETUP"));

        blocks.Add(Block(
            "L0_B2_SETUP",
            new[]
            {
                N("Two intersections in."),
                N("The girl has been watching the street. Streetlights slide across her face and pass."),
                Q("How long have you been driving this route?"),
                N("She turns toward the front. This one is a real question.")
            },
            "L0_B2_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L0_B2_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L0_B2_A", "Long enough.", 0, "L0_B2_A_REPLY"),
                Option("L0_B2_B", "Hard to say. Every night, though.", 1, "L0_B2_B_REPLY"),
                Option("L0_B2_C", "Does it matter?", 1, "L0_B2_C_REPLY")
            },
            -1,
            "L0_B2_MISS"));

        blocks.Add(Block("L0_B2_A_REPLY", new[] { Q("So you must know every shortcut by now."), N("She says it like she's confirming something, though she doesn't say what. Her eyes go back to the window.") }, "L0_B3_SETUP"));
        blocks.Add(Block("L0_B2_B_REPLY", new[] { Q("Every night..."), N("She repeats it quietly. Something in the words lands differently for her. She looks away."), Q("Hm.") }, "L0_B3_SETUP"));
        blocks.Add(Block("L0_B2_C_REPLY", new[] { N("The girl pauses. Then a quiet laugh."), Q("It matters. I just haven't decided yet."), N("She says it like it slipped out. She doesn't walk it back.") }, "L0_B3_SETUP"));
        blocks.Add(Block("L0_B2_MISS", new[] { N("Countdown ends. No response registered. The girl waits a beat."), Q("Never mind."), N("No irritation. She simply pulls back, like closing a door gently.") }, "L0_B3_SETUP"));

        blocks.Add(Block(
            "L0_B3_SETUP",
            new[]
            {
                N("Red light. The cab stops."),
                N("Neon from a shopfront paints a red strip across the window."),
                Q("Oh -- by the way."),
                N("Like she just remembered something minor."),
                Q("The options -- you can click them too. With the mouse."),
                N("She pauses, aware that sentence sounded a little odd, but she doesn't explain it."),
                Q("Just so you know. You don't have to use the keyboard."),
                N("Green light. The cab rolls forward."),
                Q("Busy night for you?")
            },
            "L0_B3_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L0_B3_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L0_B3_A", "Decent. You're what, the fourth?", 0, "L0_B3_A_REPLY"),
                Option("L0_B3_B", "Quiet nights have the better passengers.", 1, "L0_B3_B_SETUP"),
                Option("L0_B3_C", "Not many. Fine by me.", 0, "L0_B3_C_REPLY")
            },
            0,
            "L0_B3_MISS"));

        blocks.Add(Block("L0_B3_A_REPLY", new[] { Q("The fourth..."), N("She considers this -- what it means to be someone's fourth stranger of the night. Doesn't say it out loud."), Q("That's not bad.") }, "L0_B4_SETUP"));
        blocks.Add(Block("L0_B3_C_REPLY", new[] { Q("Fine by you..."), N("She nods. Finds the answer reasonable."), Q("Yeah. I think I prefer fewer people too.") }, "L0_B4_SETUP"));
        blocks.Add(Block("L0_B3_MISS", new[] { N("The girl doesn't get a response. She makes a small sound of acknowledgment."), Q("Just asking."), N("She turns back to the window. The topic passes.") }, "L0_B4_SETUP"));

        blocks.Add(Block(
            "L0_B3_B_SETUP",
            new[]
            {
                N("The girl looks over."),
                Q("Better how?")
            },
            "L0_B3_B_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L0_B3_B_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L0_B3_B1", "The ones who go quiet halfway through.", 1, "L0_B3_B1_REPLY"),
                Option("L0_B3_B2", "Hard to say. I know it when I see it.", 1, "L0_B3_B2_REPLY")
            },
            0,
            "L0_B3_B_MISS"));

        blocks.Add(Block("L0_B3_B1_REPLY", new[] { N("The girl pauses."), Q("...Does that include me."), N("Not quite a question. She seems a little surprised she said it. She turns back to the window.") }, "L0_B4_SETUP"));
        blocks.Add(Block("L0_B3_B2_REPLY", new[] { Q("That answer..."), N("A beat."), Q("Okay. Fair enough.") }, "L0_B4_SETUP"));
        blocks.Add(Block("L0_B3_B_MISS", new[] { N("The girl waits. She smiles faintly."), Q("Never mind. I was just asking.") }, "L0_B4_SETUP"));

        blocks.Add(Block(
            "L0_B4_SETUP",
            new[]
            {
                N("The cab turns into the street near the Exhibition Center. A few lights still on in the distance."),
                N("The girl glances out, adjusts the strap of her bag."),
                Q("Almost there, right?"),
                N("She doesn't move to get out. Her hand rests on her bag. She's looking ahead."),
                Q("When you can't figure something out -- but you still have to decide --"),
                N("She stops. Choosing her words, or deciding whether to say them at all."),
                Q("What do you usually do?")
            },
            "L0_B4_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L0_B4_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L0_B4_A", "Do it first. Figure it out after.", 1, "L0_END_WARM"),
                Option("L0_B4_B", "Whichever one you'll regret less.", 1, "L0_END_NEUTRAL_WARM"),
                Option("L0_B4_C", "Maybe you just pick one.", 0, "L0_END_NEUTRAL")
            },
            -2,
            "L0_END_COLD"));

        blocks.Add(Block(
            "L0_END_WARM",
            new[]
            {
                N("The girl looks at him."),
                Q("Do it first..."),
                N("She turns the phrase over quietly."),
                Q("Okay. Thanks."),
                N("She pushes the door open. Stops for a second. Doesn't look back. Walks toward the Exhibition Center.")
            },
            "END"));

        blocks.Add(Block(
            "L0_END_NEUTRAL_WARM",
            new[]
            {
                Q("Regret less..."),
                N("She repeats it softly, like testing it as a unit of measurement."),
                Q("Alright. We're here. Thanks."),
                N("She gets out. Her step is steadier than when she got in.")
            },
            "END"));

        blocks.Add(Block(
            "L0_END_NEUTRAL",
            new[]
            {
                Q("Maybe you just pick one..."),
                N("She nods. Doesn't disagree."),
                Q("Yeah. Okay. Thanks."),
                N("She gets out. No pause.")
            },
            "END"));

        blocks.Add(Block(
            "L0_END_COLD",
            new[]
            {
                N("Timer expires. The cab has stopped."),
                N("The girl waits a moment. Then she opens the door."),
                Q("Good night."),
                N("She gets out. She doesn't wait for anything. Steady pace.")
            },
            "END"));
    }

    private void LoadLevel2MargaretData()
    {
        blocks.Add(Block(
            "L2_B1_SETUP",
            new[]
            {
                N("Just past midnight. The cab waits outside a low brick administration building."),
                N("The rear door opens. Margaret gets in carefully, one hand on the doorframe."),
                N("She sets an old leather briefcase upright on the seat beside her."),
                Q("Eastlake. The apartments at the end of Oak Row, please."),
                N("She does not reach for her phone. Her hand rests on the briefcase."),
                Q("Take the long way, if you don't mind."),
                Q("I'd rather not arrive too quickly.")
            },
            "L2_B1_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L2_B1_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L2_B1_A_LONGWAY", "The long way it is.", 0, "L2_B1_A_REPLY"),
                Option("L2_B1_B_LONGDAY", "Long day?", 1, "L2_B1_B_REPLY"),
                Option("L2_B1_C_QUIET", "I know a quiet route.", 0, "L2_B1_C_REPLY")
            },
            0,
            "L2_B1_MISS"));

        blocks.Add(Block("L2_B1_A_REPLY", new[] { N("She nods, just once. The cab pulls out."), Q("Thank you."), N("Said after a long day, when the word has more weight than usual.") }, "L2_B2_SETUP"));
        blocks.Add(Block("L2_B1_B_REPLY", new[] { N("A faint, tired smile appears in the rearview mirror."), Q("Long enough."), N("She looks at the briefcase, not the window.") }, "L2_B2_SETUP"));
        blocks.Add(Block("L2_B1_C_REPLY", new[] { Q("That would be kind."), N("She settles back slightly. Her shoulders come down a fraction.") }, "L2_B2_SETUP"));
        blocks.Add(Block("L2_B1_MISS", new[] { N("Margaret does not seem to need a response. The cab moves."), N("She watches the building lights pass.") }, "L2_B2_SETUP"));

        blocks.Add(Block(
            "L2_B2_SETUP",
            new[]
            {
                N("A few minutes into the ride. The streets are emptying."),
                N("Margaret has been quiet - not uncomfortable, just somewhere else."),
                Q("I had a meeting tonight."),
                Q("Decisions about a department. Whether it stays or it doesn't."),
                N("Her hand moves on the briefcase, then settles."),
                Q("There was a student I kept thinking about, while we talked."),
                Q("I didn't say her name. I rarely do, in those rooms - it doesn't help them."),
                Q("Sometimes I wonder if it doesn't help me, either.")
            },
            "L2_B2_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L2_B2_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L2_B2_A_STUDENT", "What was the student working on?", 1, "L2_B2_A_REPLY"),
                Option("L2_B2_B_NAMES", "It must be hard to keep names out.", 0, "L2_B2_B_REPLY"),
                Option("L2_B2_C_ROOM", "Sounds like the room had already decided.", 0, "L2_B2_C_REPLY"),
                Option("L2_B2_D_OUTCOME", "Which way did it go?", 1, "L2_B2_D_REPLY", keyword: true),
                Option("L2_B2_E_STILL", "It sounds like you're still in that room.", 1, "L2_B2_E_REPLY", unlocked: true, requiredAffection: 2)
            },
            -1,
            "L2_B2_MISS"));

        blocks.Add(Block("L2_B2_A_REPLY", new[] { N("A pause - not defensive. She is choosing what to share."), Q("A piece. Something small. I thought it was good."), Q("I didn't tell her I thought so."), N("She says that last part as though noticing it for the first time.") }, "L2_B3_SETUP"));
        blocks.Add(Block("L2_B2_B_REPLY", new[] { Q("Names change the room."), N("She looks out at a dark storefront."), Q("People stop hearing the question and start weighing the person.") }, "L2_B3_SETUP"));
        blocks.Add(Block("L2_B2_C_REPLY", new[] { N("A small look in the mirror. It is almost approval."), Q("Rooms can do that before anyone speaks."), Q("By the time the vote arrives, everyone is only naming what happened earlier.") }, "L2_B3_SETUP"));
        blocks.Add(Block("L2_B2_D_REPLY", new[] { N("She is quiet for one full intersection."), Q("It closed."), Q("Not tonight, officially. But tonight was when it became closed."), N("Her hand tightens on the briefcase.") }, "L2_B3_SETUP"));
        blocks.Add(Block("L2_B2_E_REPLY", new[] { N("She exhales. Not quite a laugh."), Q("Yes."), Q("Some meetings have a way of coming home with you.") }, "L2_B3_SETUP"));
        blocks.Add(Block("L2_B2_MISS", new[] { N("The cab continues through a long green light."), Q("It is not an interesting story."), N("She says it too carefully for it to be true.") }, "L2_B3_SETUP"));

        blocks.Add(Block(
            "L2_B3_SETUP",
            new[]
            {
                N("Rain gathers in the seams of the windshield. Margaret watches the city pass in reflected pieces."),
                Q("People talk as if regret has a clean shape."),
                Q("As if you can put it in one hand and point at it."),
                Q("I have never found it to be that courteous."),
                N("A pause. Her thumb moves along the briefcase clasp."),
                Q("Usually it is only the thing you didn't say in time.")
            },
            "L2_B3_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L2_B3_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L2_B3_A_UNSAID", "The things I didn't say.", 1, "L2_B3_A_REPLY"),
                Option("L2_B3_B_DID", "What I did. At least it's honest.", 0, "L2_B3_B_REPLY"),
                Option("L2_B3_C_OWED", "Depends who I owed the words to.", 1, "L2_B3_C_REPLY"),
                Option("L2_B3_D_SOMEONE", "Was there someone you didn't say something to?", 1, "L2_B3_D_REPLY", keyword: true),
                Option("L2_B3_E_NIGHT", "Whichever one keeps you up at night.", 1, "L2_B3_E_REPLY", unlocked: true, requiredAffection: 3)
            },
            -1,
            "L2_B3_MISS"));

        blocks.Add(Block("L2_B3_A_REPLY", new[] { Q("Yes."), N("She answers too quickly, then looks away."), Q("That is usually where I begin, too.") }, "L2_B4_SETUP"));
        blocks.Add(Block("L2_B3_B_REPLY", new[] { Q("Honesty has its uses."), Q("It also has a talent for arriving late."), N("There is no rebuke in it. Only experience.") }, "L2_B4_SETUP"));
        blocks.Add(Block("L2_B3_C_REPLY", new[] { N("She considers that with unusual care."), Q("That is closer to the matter, I think."), Q("Who was owed the words.") }, "L2_B4_SETUP"));
        blocks.Add(Block("L2_B3_D_REPLY", new[] { N("Her face changes slightly in the mirror."), Q("There is always someone, isn't there?"), Q("The question is whether saying it now would help anyone but yourself.") }, "L2_B4_SETUP"));
        blocks.Add(Block("L2_B3_E_REPLY", new[] { N("For the first time, Margaret looks directly at the mirror."), Q("That is a cruelly accurate measure."), Q("Yes. That one.") }, "L2_B4_SETUP"));
        blocks.Add(Block("L2_B3_MISS", new[] { N("Margaret lets the question go unanswered."), Q("No need to make a philosophy of it."), N("But she sounds like she already has.") }, "L2_B4_SETUP"));

        blocks.Add(Block(
            "L2_B4_SETUP",
            new[]
            {
                N("The road bends toward the older part of the city. Neon and old brick slide across the glass."),
                Q("She submitted something for an exhibition."),
                Q("Or rather - I submitted it for her."),
                N("Margaret's hand is still on the briefcase."),
                Q("Without telling her."),
                Q("Hartwell had one last student exhibition before the department went quiet."),
                Q("I told myself I was only making sure the work was seen.")
            },
            "L2_B4_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L2_B4_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L2_B4_A_WHY", "Why didn't you tell her?", 1, "L2_B4_A_REPLY"),
                Option("L2_B4_B_FINDOUT", "Will she find out it was you?", 0, "L2_B4_B_REPLY"),
                Option("L2_B4_D_KNOWN", "She might rather have known.", 1, "L2_B4_D_REPLY", keyword: true),
                Option("L2_B4_E_HARTWELL", "Hartwell - that's the school the news was about, isn't it?", 1, "L2_B4_E_REPLY", keyword: true),
                Option("L2_B4_F_TONIGHT", "You were going to tell her tonight, weren't you?", 1, "L2_B4_F_REPLY", unlocked: true, requiredOptionId: "L2_B2_A_STUDENT"),
                Option("L2_B4_G_COST", "It must have cost you, sending it in for her.", 1, "L2_B4_G_REPLY", unlocked: true, requiredAffection: 4)
            },
            -1,
            "L2_B4_MISS"));

        blocks.Add(Block("L2_B4_A_REPLY", new[] { N("Margaret gives a small, humorless smile."), Q("Because then it would have become a conversation about permission."), Q("And I was afraid I would lose my nerve in the name of respecting hers.") }, "L2_B5_SETUP"));
        blocks.Add(Block("L2_B4_B_REPLY", new[] { Q("Eventually, perhaps."), Q("There is a calendar to being found out."), N("She says it like a joke. It does not land as one.") }, "L2_B5_SETUP"));
        blocks.Add(Block("L2_B4_D_REPLY", new[] { N("She closes her eyes for a second, then opens them."), Q("Yes."), Q("That is the part I keep coming back to.") }, "L2_B5_SETUP"));
        blocks.Add(Block("L2_B4_E_REPLY", new[] { N("The name sits in the cab for a moment."), Q("Yes. Hartwell."), Q("It sounds smaller when someone else says it."), N("She looks back to the window.") }, "L2_B5_SETUP"));
        blocks.Add(Block("L2_B4_F_REPLY", new[] { N("Margaret's hand stops moving on the briefcase."), Q("I was working up to it."), Q("That is a very elegant phrase for failing to do something simple.") }, "L2_B5_SETUP"));
        blocks.Add(Block("L2_B4_G_REPLY", new[] { N("She gives you a long look in the mirror."), Q("Some things cost less than not doing them."), Q("I am not sure which one this was.") }, "L2_B5_SETUP"));
        blocks.Add(Block("L2_B4_MISS", new[] { N("The cab passes under a row of amber streetlights."), Q("It was not my finest professional judgment."), N("She says it like the safer version of a harder sentence.") }, "L2_B5_SETUP"));

        blocks.Add(Block(
            "L2_B5_SETUP",
            new[]
            {
                N("The city thins. Apartment windows appear one by one in the distance."),
                Q("People assume the worst regret is not having stopped someone."),
                Q("From the wrong path, the wrong choice, whatever the word is for it."),
                Q("It isn't, for me. Not the stopping."),
                N("A pause. She is choosing whether to finish the thought."),
                Q("My biggest regret isn't that I didn't stop someone."),
                Q("It's the time I should have answered, and didn't."),
                Q("I should have told her I thought it was good. While there was still time to.")
            },
            "L2_B5_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L2_B5_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L2_B5_A_WHO", "Who was it?", 0, "L2_B5_A_REPLY"),
                Option("L2_B5_B_STILL", "Could you still say it?", 1, "L2_B5_B_REPLY"),
                Option("L2_B5_C_SORRY", "I'm sorry.", 1, "L2_B5_C_REPLY"),
                Option("L2_B5_D_MEANT", "She'll know you meant to.", 1, "L2_B5_D_REPLY", unlocked: true, requiredAffection: 5),
                Option("L2_B5_E_ANSWER", "The one you didn't answer.", 1, "L2_B5_E_REPLY", unlocked: true, requiredOptionId: "L2_B3_E_NIGHT")
            },
            -1,
            "L2_B5_MISS"));

        blocks.Add(Block("L2_B5_A_REPLY", new[] { Q("Someone who would have done well to hear it earlier."), N("She does not give a name. She has never given a name.") }, "L2_B6_SETUP"));
        blocks.Add(Block("L2_B5_B_REPLY", new[] { Q("I might."), Q("I think I might, tomorrow."), N("Said as though hearing the possibility for the first time.") }, "L2_B6_SETUP"));
        blocks.Add(Block("L2_B5_C_REPLY", new[] { Q("Thank you."), N("She looks at the mirror. The thanks is for hearing it as something that needed sitting with, not solving.") }, "L2_B6_SETUP"));
        blocks.Add(Block("L2_B5_D_REPLY", new[] { N("Her eyes go to the rearview, briefly. Something in her expression softens."), Q("That's a kind thing to assume."), Q("I'll try to make it true.") }, "L2_B6_SETUP"));
        blocks.Add(Block("L2_B5_E_REPLY", new[] { N("The driver has done what she did not: finished the sentence."), Q("Yes."), Q("An afternoon, four years ago. She asked me whether she should keep going."), Q("I said something about discipline. I should have said something else.") }, "L2_B6_SETUP"));
        blocks.Add(Block("L2_B5_MISS", new[] { N("She nods very slightly at the window."), Q("Yes. Some things are like that.") }, "L2_B6_SETUP"));

        blocks.Add(Block(
            "L2_B6_SETUP",
            new[]
            {
                N("The cab pulls up at the end of Oak Row. A small block of apartments waits under a single lit window."),
                N("Margaret does not move immediately. She places one hand flat on the briefcase."),
                Q("Thank you. For taking the long way."),
                Q("There used to be more lights on, on this row."),
                Q("People keep them off now to save what they can."),
                N("She opens the door. Sets one foot down. Does not get out yet.")
            },
            "L2_B6_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L2_B6_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L2_B6_A_TELL", "Tell her, tomorrow.", 1, "L2_END_TELL", unlocked: true, requiredAffection: 5),
                Option("L2_B6_B_REST", "Get some rest.", 1, "L2_END_REST"),
                Option("L2_B6_C_GOODNIGHT", "Goodnight.", 0, "L2_END_GOODNIGHT"),
                Option("L2_B6_D_HARTWELL", "I won't tell anyone about Hartwell.", 1, "L2_END_HARTWELL", unlocked: true, requiredOptionId: "L2_B4_E_HARTWELL"),
                Option("L2_B6_E_GOOD", "It was good of you, sending it in for her.", 1, "L2_END_GOOD", unlocked: true, requiredAffection: 3)
            },
            -1,
            "L2_END_MISS",
            0f));

        blocks.Add(Block("L2_END_TELL", new[] { N("She pauses with one foot still in the cab."), Q("I'll try."), N("She says it without conviction and without pretence. Both, at once. She gets out.") }, "END"));
        blocks.Add(Block("L2_END_REST", new[] { Q("I will. Eventually."), N("She gets out, adjusts the briefcase, and starts toward the door.") }, "END"));
        blocks.Add(Block("L2_END_GOODNIGHT", new[] { Q("Goodnight."), N("She steps out, closes the door without slamming it, and walks at her own pace.") }, "END"));
        blocks.Add(Block("L2_END_HARTWELL", new[] { N("She pauses fully. Looks at the rearview, briefly."), Q("I know."), Q("Thank you for that, too."), N("She gets out. Walks more slowly than before, but no less steadily.") }, "END"));
        blocks.Add(Block("L2_END_GOOD", new[] { N("Her hand pauses on the briefcase strap."), Q("I hope so."), Q("Drive home safely."), N("She gets out and closes the door at her usual pace.") }, "END"));
        blocks.Add(Block("L2_END_MISS", new[] { N("She gives the briefcase one last look, as though making sure she has not left anything behind."), N("Then she steps out. The door closes softly.") }, "END"));
    }

    private void LoadLevel3OwenData()
    {
        blocks.Add(Block(
            "L3_B1_SETUP",
            new[]
            {
                N("1:17 a.m. Outside the Qinghe Exhibition Center. The main doors are locked now. A side entrance is still open, throwing a narrow strip of white light onto the pavement."),
                N("Owen comes out backward, balancing two paper tubes under one arm and a flat portfolio case in the other hand. He almost drops the tubes, catches them with his chin, then laughs once at himself."),
                N("He opens the rear door with some difficulty and gets in with too many objects. The tubes roll against the seat. He gathers them quickly."),
                Q("Sorry -- sorry. I have, like, four hands worth of things and two hands."),
                N("He shuts the door with his elbow. One paper tube knocks gently against the glass."),
                Q("Riverside Studios. The old warehouse ones, near the canal."),
                N("A beat. He checks the tubes, then the portfolio, then the inside pocket of his coat. His fingers pause there. He does not take anything out."),
                Q("Long night for you too, huh?"),
                N("He says it easily, like conversation is something he reaches for before thinking.")
            },
            "L3_B1_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L3_B1_RESPONSE",
            System.Array.Empty<string>(),
            CreateLevel3Block1Options(),
            -1,
            "L3_B1_MISS"));

        blocks.Add(Block(
            "L3_B1_A_REPLY",
            new[]
            {
                N("Owen looks at the tubes, then the window, then back toward the mirror as if realizing the obvious."),
                Q("Yeah. Last night of it. We stayed after to take everything down."),
                N("He says \"last night\" lightly, but the phrase has a small aftertaste.")
            },
            "L3_B2_SETUP"));

        blocks.Add(Block(
            "L3_B1_B_REPLY",
            new[]
            {
                Q("Thanks."),
                N("He settles the portfolio upright by his knees."),
                Q("It's not far. Unless the city decides to be weird about traffic at one in the morning, which it does sometimes.")
            },
            "L3_B2_SETUP"));

        blocks.Add(Block("L3_B1_C_REPLY", GetLevel3Block1CReplyLines(), "L3_B2_SETUP"));
        blocks.Add(Block(
            "L3_B1_MISS",
            new[]
            {
                N("Owen doesn't seem offended. He fills the space himself."),
                Q("Quiet cab. Okay. That's okay."),
                N("A beat."),
                Q("Might be better, actually.")
            },
            "L3_B2_SETUP"));

        blocks.Add(Block(
            "L3_B2_SETUP",
            new[]
            {
                N("The cab pulls away from the Exhibition Center. Through the rear window, the side entrance shrinks to a white rectangle."),
                N("Owen leans one paper tube against his shoulder, then adjusts it so it doesn't slide."),
                Q("It was a small show. Not small in a bad way. Small like -- everyone can hear everyone pretending not to be nervous."),
                N("He laughs quietly."),
                Q("Photography mostly. Mine was the night series. Long exposures, empty streets, convenience store lights, people walking through frame like ghosts because they wouldn't stand still."),
                N("He looks out the window. For the first time, he slows down."),
                Q("There was a board by the exit. People could leave notes. I thought that was a stupid idea when we put it up."),
                N("He taps his coat pocket once, barely."),
                Q("It wasn't.")
            },
            "L3_B2_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L3_B2_RESPONSE",
            System.Array.Empty<string>(),
            CreateLevel3Block2Options(),
            -1,
            "L3_B2_MISS"));

        blocks.Add(Block(
            "L3_B2_A_REPLY",
            new[]
            {
                Q("Mostly normal things."),
                N("He counts on his fingers."),
                Q("\"Beautiful light.\" \"Loved the canal photo.\" One person wrote \"too much blue,\" which, fair."),
                N("He smiles."),
                Q("And then one note that didn't really belong with the others.")
            },
            "L3_B2_RADIO_ROCK_SETUP"));

        blocks.Add(Block(
            "L3_B2_B_REPLY",
            new[]
            {
                Q("Because I thought people would either be polite or clever. Both are useless, mostly."),
                N("He shifts the portfolio against his knees."),
                Q("But sometimes people forget to be either. That's when a note gets dangerous.")
            },
            "L3_B2_RADIO_ROCK_SETUP"));

        blocks.Add(Block(
            "L3_B2_C_REPLY",
            new[]
            {
                Q("They do. They lie less."),
                N("A beat."),
                Q("Or maybe they lie better. I haven't decided.")
            },
            "L3_B2_RADIO_ROCK_SETUP"));

        blocks.Add(Block(
            "L3_B2_D_REPLY",
            new[]
            {
                N("Owen looks toward the mirror. The quickness leaves him for a second."),
                Q("That's a strange thing to guess."),
                N("He looks down at his coat pocket."),
                Q("Yeah. I think so.")
            },
            "L3_B2_RADIO_ROCK_SETUP"));

        blocks.Add(Block(
            "L3_B2_MISS",
            new[]
            {
                N("Owen nods to himself, as if continuing a conversation that only needed one person."),
                Q("Anyway, it was a good night. I think."),
                N("The \"I think\" comes late.")
            },
            "L3_B2_RADIO_ROCK_SETUP"));

        blocks.Add(Block(
            "L3_B2_RADIO_ROCK_SETUP",
            GetLevel3RadioRockSetupLines(),
            "L3_B2_RADIO_ROCK_CONTINUE"));

        blocks.Add(Block(
            "L3_B2_RADIO_ROCK_CONTINUE",
            GetLevel3RadioRockContinueLines(),
            "L3_B3_SETUP_ROCK"));

        blocks.Add(Block(
            "L3_B4_RADIO_CLASSICAL_SETUP",
            GetLevel3RadioClassicalSetupLines(),
            "L3_B4_RADIO_CLASSICAL_CONTINUE"));

        blocks.Add(Block(
            "L3_B4_RADIO_CLASSICAL_CONTINUE",
            GetLevel3RadioClassicalContinueLines(),
            "L3_B5_SETUP"));

        blocks.Add(Block(
            "L3_B3_SETUP_ROCK",
            GetLevel3Block3SetupLines(
                N("A low rock track plays under the road noise. Owen taps once against his knee, then stops when he realizes he's doing it.")),
            "L3_B3_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L3_B3_RESPONSE",
            System.Array.Empty<string>(),
            CreateLevel3Block3Options(),
            -1,
            "L3_B3_MISS"));

        blocks.Add(Block(
            "L3_B3_A_REPLY",
            new[]
            {
                Q("No."),
                N("Immediate. Then softer."),
                Q("I mean -- I knew she might come. I told her about it. I just didn't think she would."),
                N("A beat."),
                Q("That's a stupid thing to admit, maybe.")
            },
            "L3_B4_SETUP"));

        blocks.Add(Block(
            "L3_B3_B_REPLY",
            new[]
            {
                N("Owen gives a small, helpless laugh."),
                Q("Because she's Vera."),
                N("He hears how that sounds and tries again."),
                Q("Because if she stayed, someone might ask her what she meant.")
            },
            "L3_B4_SETUP"));

        blocks.Add(Block(
            "L3_B3_C_REPLY",
            new[]
            {
                N("Owen looks up."),
                Q("Yeah."),
                N("A short pause."),
                Q("You know about that?"),
                N("He doesn't wait for an answer he could reasonably get."),
                Q("Then you know why tonight felt... weird. Like everyone was taking pictures at a funeral and pretending it was still an opening.")
            },
            "L3_B4_SETUP"));

        blocks.Add(Block(
            "L3_B3_D_REPLY",
            new[]
            {
                N("Owen turns that over."),
                Q("Did she."),
                N("Not a question to the driver. More like he is matching it to something he already suspects."),
                Q("There is someone she was waiting on, I think. Not me."),
                N("A beat."),
                Q("I used to be relieved by that. Tonight it just made me feel late.")
            },
            "L3_B4_SETUP"));

        blocks.Add(Block(
            "L3_B3_E_REPLY",
            new[]
            {
                N("Owen goes still for half a second."),
                Q("Yeah."),
                N("He says it like the thought hurts because it makes sense."),
                Q("That's the thing. It wasn't hidden. It was just placed where I would only see it when everything was over.")
            },
            "L3_B4_SETUP"));

        blocks.Add(Block(
            "L3_B3_MISS",
            new[]
            {
                N("Owen exhales. The name remains in the cab anyway."),
                Q("Anyway. Vera came by."),
                N("He repeats it as if repetition might make the fact easier to hold.")
            },
            "L3_B4_SETUP"));

        blocks.Add(Block(
            "L3_B4_SETUP",
            new[]
            {
                N("They pass a closed convenience store. Inside, one fluorescent tube flickers above empty aisles. Owen watches it until it disappears."),
                Q("The board was near the exit. Cork, cheap frame, bad pins. I was going to throw it away after tonight."),
                N("He slides two fingers into his coat pocket and touches the folded note but still does not remove it."),
                Q("Her note was folded once. Not like the others. The others were just stuck there flat, like receipts."),
                N("A pause."),
                Q("I knew it was hers before I read it."),
                N("He smiles faintly, but it does not last."),
                Q("She has this way of making even paper look like it's hesitating.")
            },
            "L3_B4_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L3_B4_RESPONSE",
            System.Array.Empty<string>(),
            CreateLevel3Block4Options(),
            -1,
            "L3_B4_MISS"));

        blocks.Add(Block("L3_B4_A_REPLY", GetLevel3Block4AReplyLines(), "L3_B4_RADIO_CLASSICAL_SETUP"));
        blocks.Add(Block(
            "L3_B4_B_REPLY",
            new[]
            {
                N("Owen pats his coat pocket once."),
                Q("Yeah."),
                N("A beat."),
                Q("Which is very normal behavior. Keeping folded paper in your coat like evidence."),
                N("He laughs once, then stops."),
                Q("I don't know what else to do with it yet.")
            },
            "L3_B4_RADIO_CLASSICAL_SETUP"));

        blocks.Add(Block(
            "L3_B4_C_REPLY",
            new[]
            {
                Q("No. Knowing someone doesn't give you the right translation."),
                N("He says it faster than expected. Then softens."),
                Q("Sorry. I just -- no. I don't know."),
                N("A beat."),
                Q("I know what I hope she meant.")
            },
            "L3_B4_RADIO_CLASSICAL_SETUP"));

        blocks.Add(Block(
            "L3_B4_D_REPLY",
            new[]
            {
                N("Owen looks at the mirror. This one lands hard."),
                Q("Yeah."),
                N("A long pause."),
                Q("That's exactly what it felt like. Like the note was the second sentence. Like the first one got lost somewhere before she reached the door.")
            },
            "L3_B4_RADIO_CLASSICAL_SETUP"));

        blocks.Add(Block("L3_B4_E_REPLY", GetLevel3Block4EReplyLines(), "L3_B4_RADIO_CLASSICAL_SETUP"));
        blocks.Add(Block("L3_B4_F_REPLY", GetLevel3Block4FReplyLines(), "L3_B4_RADIO_CLASSICAL_SETUP"));
        blocks.Add(Block(
            "L3_B4_MISS",
            new[]
            {
                N("Owen looks at the window. The topic does not disappear, but he carries it alone."),
                Q("It was just a note. People leave notes."),
                N("He does not believe this.")
            },
            "L3_B4_RADIO_CLASSICAL_SETUP"));

        blocks.Add(Block(
            "L3_B5_SETUP",
            new[]
            {
                N("The cab turns toward the canal road. Water appears between buildings in narrow black strips. The city is quieter here."),
                N("Owen finally takes the note out. We do not see the full text. It is folded once, as described. He holds it but does not unfold it."),
                Q("I keep thinking about the part she didn't write."),
                N("He looks at the folded paper, then folds it again along the same crease."),
                Q("That's a terrible habit. Reading blank space like it owes you something."),
                N("A beat."),
                Q("But there was a lot of blank space."),
                N("He puts the note on top of the portfolio case. His hand stays over it."),
                Q("It wasn't a goodbye. I don't think."),
                N("He waits, and for once he does not immediately fill the silence.")
            },
            "L3_B5_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L3_B5_RESPONSE",
            System.Array.Empty<string>(),
            CreateLevel3Block5Options(),
            -2,
            "L3_B5_MISS",
            8f));

        blocks.Add(Block("L3_B5_A_REPLY", GetLevel3Block5AReplyLines(), "L3_B6_SETUP"));
        blocks.Add(Block("L3_B5_B_REPLY", GetLevel3Block5BReplyLines(), "L3_B6_SETUP"));
        blocks.Add(Block(
            "L3_B5_C_REPLY",
            new[]
            {
                N("Owen stares at the phone in his hand. He had not realized he picked it up."),
                Q("I can."),
                N("A beat."),
                Q("That's the awful part. I can, which means if I don't, that becomes an answer too.")
            },
            "L3_B6_SETUP"));

        blocks.Add(Block(
            "L3_B5_D_REPLY",
            new[]
            {
                N("Owen does not speak for a moment."),
                Q("Yeah."),
                N("The word is quiet."),
                Q("Not in the show. Not in the school. In her own life, maybe."),
                N("He looks at the folded note."),
                Q("She didn't phrase it like that. She wouldn't. But yeah.")
            },
            "L3_B6_SETUP"));

        blocks.Add(Block(
            "L3_B5_E_REPLY",
            new[]
            {
                N("Owen's eyes move to the mirror."),
                Q("No."),
                N("A beat."),
                Q("I don't think she did."),
                N("He considers whether to say more."),
                Q("The note mentioned waiting on someone who was better at silence than kindness. Could be anyone, technically."),
                N("He gives a small humorless smile."),
                Q("It wasn't anyone.")
            },
            "L3_B6_SETUP"));

        blocks.Add(Block(
            "L3_B5_F_REPLY",
            new[]
            {
                N("Owen looks at the note as if it has changed shape."),
                Q("That's..."),
                N("He doesn't finish. Then nods once."),
                Q("That's probably true."),
                N("He folds the note carefully and returns it to his pocket, but this time it looks less like hiding.")
            },
            "L3_B6_SETUP"));

        blocks.Add(Block(
            "L3_B5_MISS",
            new[]
            {
                N("Owen waits a little too long. Then fills the space, but more quietly than before."),
                Q("I didn't think she'd write something like that."),
                N("He puts the note away."),
                Q("That's all.")
            },
            "L3_B6_SETUP"));

        blocks.Add(Block("L3_B6_SETUP", GetLevel3Block6SetupLines(), "L3_B6_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L3_B6_RESPONSE",
            System.Array.Empty<string>(),
            CreateLevel3Block6Options(),
            0,
            null,
            -1f));

        blocks.Add(Block(
            "L3_END_REPLY",
            new[]
            {
                N("Owen nods slowly."),
                Q("Write first. Then show up."),
                N("He repeats it like a plan simple enough to survive morning."),
                Q("Okay."),
                N("He opens the door, steps out, then leans back in to collect one forgotten tube."),
                Q("Thanks.")
            },
            "END"));

        blocks.Add(Block(
            "L3_END_WARMEST",
            new[]
            {
                N("Owen looks at the mirror. The line lands exactly where it needs to."),
                Q("Yeah."),
                N("A beat."),
                Q("Yeah, okay."),
                N("He gets out, then stands beside the open door for a second, phone in hand. He is not typing yet. But he has stopped delaying.")
            },
            "END"));

        blocks.Add(Block(
            "L3_END_PUSHED",
            new[]
            {
                N("Owen gives a short laugh. Not amused -- caught."),
                Q("That's mean."),
                N("A beat."),
                Q("True, though."),
                N("He pockets the phone, then immediately takes it out again as he steps onto the curb.")
            },
            "END"));

        blocks.Add(Block(
            "L3_END_GENTLE",
            new[]
            {
                Q("That might be enough."),
                N("He thinks about it."),
                Q("Or at least it might be the first honest sentence."),
                N("He gets out carefully, carrying everything this time.")
            },
            "END"));

        blocks.Add(Block(
            "L3_END_OPEN",
            new[]
            {
                N("Owen looks down at the phone."),
                Q("I hate when that's true."),
                N("He smiles. This one stays a little longer."),
                Q("Goodnight.")
            },
            "END"));

        blocks.Add(Block(
            "L3_END_UNCERTAIN",
            new[]
            {
                N("Owen waits. This time, the silence is not an accident. He nods once, accepting it as an answer of its own."),
                Q("Yeah."),
                N("He opens the door."),
                Q("Goodnight."),
                N("Outside, he stands under the warehouse light and looks at his phone. The cab pulls away before we see whether he types.")
            },
            "END"));
    }

    private void LoadLevel1KeywordData()
    {
        blocks.Add(Block(
            "L1_B1_SETUP",
            new[]
            {
                N("Just before midnight."),
                N("The man is already in the cab. He flagged it down mid-call."),
                N("By the time he properly settles in and closes the door, he has just hung up."),
                N("He loosens his tie slightly. Sets his bag on his knees. Looks out the window at nothing in particular."),
                Q("Weston Bridge, please."),
                Q("Past the overpass -- the offices there."),
                N("He checks his phone. Habit, not purpose. Locks it. Puts it away."),
                Q("Long night."),
                N("He says it like he is talking to himself, but it lands in the space between them.")
            },
            "L1_B1_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L1_B1_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L1_B1_A_ROUGH", "Rough one?", 1, "L1_B1_A_REPLY", keyword: true),
                Option("L1_B1_B_HOME", "Home or office?", 0, "L1_B1_B_REPLY"),
                Option("L1_B1_C_EVERYONE", "You and everyone else.", 0, "L1_B1_C_REPLY")
            },
            -1,
            "L1_B1_MISS"));

        blocks.Add(Block("L1_B1_A_REPLY", new[] { N("The man glances briefly at the rearview mirror."), Q("Not rough. Just long."), N("A distinction that matters to him. He does not explain it.") }, "L1_B2_SETUP"));
        blocks.Add(Block("L1_B1_B_REPLY", new[] { Q("Some nights, the office is home."), N("Dry. He has said that before.") }, "L1_B2_SETUP"));
        blocks.Add(Block("L1_B1_C_REPLY", new[] { N("Something shifts faintly in his expression, between acknowledgment and mild surprise."), Q("Yeah. Suppose so."), N("He settles back. Looks out the window again.") }, "L1_B2_SETUP"));
        blocks.Add(Block("L1_B1_MISS", new[] { N("The man does not need a response. He was not quite asking for one."), N("He looks out the window. The cab moves.") }, "L1_B2_SETUP"));

        blocks.Add(Block(
            "L1_B2_SETUP",
            new[]
            {
                N("Two or three minutes in."),
                N("The man has been quiet."),
                N("He picks up his phone, looks at something, sets it face-down on his knee."),
                Q("Signed a proposal tonight I wasn't completely happy with."),
                N("He says it evenly. Not a complaint. More like reading something off a ledger."),
                Q("It'll go through fine. Just not what I would have made."),
                N("He is looking ahead. His thumb moves once against the back of his phone, then stops.")
            },
            "L1_B2_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L1_B2_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L1_B2_A_HAPPENS", "Happens to everyone.", 0, "L1_B2_A_REPLY"),
                Option("L1_B2_B_MADE", "What would you have made?", 2, "L1_B2_B_REPLY", keyword: true),
                Option("L1_B2_C_WORKS", "Does it matter, if it works?", 0, "L1_B2_C_REPLY"),
                Option("L1_B2_D_ROUGHWHY", "You said rough. Is this why?", 2, "L1_B2_D_REPLY", unlocked: true, requiredOptionId: "L1_B1_A_ROUGH")
            },
            -3,
            "L1_B2_MISS"));

        blocks.Add(Block("L1_B2_A_REPLY", new[] { Q("It does."), N("He agrees too quickly. Like he has rehearsed that response."), Q("That's not really the point, though."), N("Said quietly, almost to himself. He does not elaborate.") }, "L1_B3_SETUP"));
        blocks.Add(Block("L1_B2_B_REPLY", new[] { N("He is quiet for a moment. The question landed somewhere."), Q("Something with less... optimisation."), N("A pause."), Q("There used to be a different answer to that question."), N("He does not continue.") }, "L1_B3_SETUP"));
        blocks.Add(Block("L1_B2_C_REPLY", new[] { N("Something shifts in his expression. Not irritation, more like recognition."), Q("That's what I told myself."), N("He looks out the window.") }, "L1_B3_SETUP"));
        blocks.Add(Block("L1_B2_D_REPLY", new[] { N("He checks the mirror and holds it slightly longer than before."), Q("...Not just this."), N("He does not elaborate. But it is the first time tonight he has not immediately walked something back.") }, "L1_B3_SETUP"));
        blocks.Add(Block("L1_B2_MISS", new[] { N("The man glances at the mirror once, then back to the window."), Q("Anyway."), N("Closed.") }, "L1_B3_SETUP"));

        blocks.Add(Block(
            "L1_B3_SETUP",
            new[]
            {
                N("The man has loosened up slightly, less like someone reading off a ledger and more like someone on a long drive home."),
                Q("I do a lot of hiring."),
                Q("Entry-level creatives, mostly."),
                N("He is not really asking. He is warming up to something."),
                Q("Portfolios are fine. Sometimes there's a real instinct there."),
                N("He rolls his sleeve up slightly."),
                Q("But the confidence -- you only get that from making something just for yourself."),
                Q("Not for a brief, not for anyone's approval."),
                Q("You can tell pretty quickly who still has it.")
            },
            "L1_B3_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L1_B3_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L1_B3_A_DONT", "What do you do with the ones who don't?", 1, "L1_B3_A_REPLY"),
                Option("L1_B3_B_TEACH", "Can you teach it?", 2, "L1_B3_B_REPLY", keyword: true),
                Option("L1_B3_C_LIKE", "Were you like that?", 2, "L1_B3_C_REPLY", keyword: true),
                Option("L1_B3_E_WRONG", "Have you been wrong about someone?", 1, "L1_B3_E_REPLY"),
                Option("L1_B3_D_DIFFERENT", "You said there was a different answer.", 2, "L1_B3_D_REPLY", unlocked: true, requiredOptionId: "L1_B2_B_MADE")
            },
            -3,
            "L1_B3_MISS"));

        blocks.Add(Block("L1_B3_A_REPLY", new[] { Q("What do you mean?"), N("Genuine question, not defensive."), Q("Oh -- hire them anyway."), N("A pause."), Q("Sometimes. The work still gets done."), N("Said without cruelty. Somehow that makes it heavier.") }, "L1_B4_SETUP"));
        blocks.Add(Block("L1_B3_B_REPLY", new[] { N("A beat. This one got him."), Q("No."), N("Immediate."), Q("You can protect it, maybe. If you're careful. But you can't install it."), N("He looks out the window after saying that.") }, "L1_B4_SETUP"));
        blocks.Add(Block("L1_B3_C_REPLY", new[] { N("He does not answer immediately."), Q("I thought so."), N("Quiet. Not bitter. Something more settled than that."), Q("Still do, on some days.") }, "L1_B4_SETUP"));
        blocks.Add(Block("L1_B3_E_REPLY", new[] { Q("Which direction?"), N("He is actually categorising."), Q("Thought they had it, turned out they didn't -- that's rare."), N("A pause."), Q("Thought they didn't, turned out they did --"), N("He does not finish. His gaze shifts slightly, then comes back."), Q("That one, maybe.") }, "L1_B4_SETUP"));
        blocks.Add(Block("L1_B3_D_REPLY", new[] { N("He lets out a quiet breath."), Q("There was."), Q("Before the briefs. Before the rooms where everyone says 'excellent' and means 'marketable.'"), N("He sounds annoyed at himself for saying that much.") }, "L1_B4_SETUP"));
        blocks.Add(Block("L1_B3_MISS", new[] { Q("Anyway."), Q("It's a useful thing to be able to spot."), N("He closes the subject himself.") }, "L1_B4_SETUP"));

        blocks.Add(Block(
            "L1_B4_SETUP",
            new[]
            {
                N("They have passed the main overpass. Not far now."),
                N("The man has been watching the city skyline."),
                Q("I went to art school, actually."),
                N("Offhand. Like it is trivia, not biography."),
                Q("Small private place. Had a decent Fine Arts program, back then."),
                N("A pause."),
                Q("I heard they're closing the department."),
                N("He says it like a weather report. No weight in his voice. But he has stopped looking out the window."),
                Q("Things change.")
            },
            "L1_B4_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L1_B4_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L1_B4_A_SHAME", "That's a shame.", 0, "L1_B4_A_REPLY"),
                Option("L1_B4_B_TOUCH", "Did you stay in touch?", 1, "L1_B4_B_REPLY"),
                Option("L1_B4_C_MISS", "Do you miss it?", 1, "L1_B4_C_REPLY", keyword: true),
                Option("L1_B4_D_PROTECT", "You said protect it. Did you?", 3, "L1_B4_D_REPLY", unlocked: true, requiredOptionId: "L1_B3_B_TEACH")
            },
            -3,
            "L1_B4_MISS"));

        blocks.Add(Block("L1_B4_A_REPLY", new[] { Q("Is it?"), N("He asks it genuinely, not rhetorically."), Q("I've been trying to decide."), N("He does not say what he has decided.") }, "L1_B5_SETUP"));
        blocks.Add(Block("L1_B4_B_REPLY", new[] { Q("Some people."), N("Noncommittal."), Q("Less than I probably should have."), N("No apology in it. That is almost worse.") }, "L1_B5_SETUP"));
        blocks.Add(Block("L1_B4_C_REPLY", new[] { N("He considers this with unusual seriousness."), Q("The school, or the version of me that was there?"), N("Not evasion. He seems to be genuinely sorting it out."), Q("Probably the second one.") }, "L1_B5_SETUP"));
        blocks.Add(Block("L1_B4_D_REPLY", new[] { N("The longest pause so far. He looks out the window."), Q("Someone messaged me this afternoon."), Q("Asked if I'd seen the news about the Academy."), N("He glances at the phone on his knee. Does not pick it up."), Q("I didn't reply."), N("Small. He moves past it."), Q("The program was called Hartwell Fine Arts. I was probably their last good year."), N("He says it like a joke. It is not one.") }, "L1_B5_SETUP"));
        blocks.Add(Block("L1_B4_MISS", new[] { Q("Like I said."), Q("Things change."), N("Closed.") }, "L1_B5_SETUP"));

        blocks.Add(Block(
            "L1_B5_SETUP",
            new[]
            {
                N("Almost at the destination. The offices are visible ahead."),
                Q("Someone sent me their work recently."),
                N("Same tone as everything else. Measured. But he brought it up out of nowhere."),
                Q("Young. Still in school, I think. Good instincts."),
                N("A pause."),
                Q("I haven't written back yet."),
                N("Flatly. No excuse offered.")
            },
            "L1_B5_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L1_B5_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L1_B5_A_WHY", "Why not?", 1, "L1_B5_A_REPLY"),
                Option("L1_B5_B_WORK", "What's the work like?", 1, "L1_B5_B_REPLY", keyword: true),
                Option("L1_B5_C_WAITING", "They're probably waiting.", 0, "L1_B5_C_REPLY"),
                Option("L1_B5_D_ARETHEY", "You said you were like that. Are they?", 2, "L1_B5_D_REPLY", unlocked: true, requiredOptionId: "L1_B3_C_LIKE")
            },
            -1,
            "L1_B5_MISS"));

        blocks.Add(Block("L1_B5_A_REPLY", new[] { N("He takes a moment."), Q("I'm not sure what to say that wouldn't sound like advice."), N("A beat."), Q("And I'm not sure advice is what they need."), N("He does not say what they need instead.") }, "L1_B6_SETUP"));
        blocks.Add(Block("L1_B5_B_REPLY", new[] { Q(GetDanielWorkLine()), N("He does not elaborate on what he means. Looks at his phone again.") }, "L1_B6_SETUP"));
        blocks.Add(Block("L1_B5_C_REPLY", new[] { Q("Probably."), N("He does not disagree. Does not move."), Q("I'll get to it."), N("Standard deflection. But he picked up his phone. Set it back down.") }, "L1_B6_SETUP"));
        blocks.Add(Block("L1_B5_D_REPLY", new[] { N("He looks at the mirror for a moment."), Q("That's a strange question."), N("A pause."), Q("...Maybe."), N("He does not say what might change if the answer is yes.") }, "L1_B6_SETUP"));
        blocks.Add(Block("L1_B5_MISS", new[] { N("He does not need a response. He is already folding the subject away."), Q("Anyway.") }, "L1_B6_SETUP"));

        blocks.Add(Block(
            "L1_B6_SETUP",
            new[]
            {
                N("The cab pulls up outside the offices."),
                N("A light is still on, two floors up."),
                N("The man looks at it. Takes his bag. Does not move to get out immediately."),
                Q("Thanks."),
                N("He checks his phone one more time."),
                Q("Still got a few hours in me."),
                N("He says it like it is a good thing. It might not be."),
                N("He opens the door, then pauses. Not dramatically, just the natural pause of someone finishing a thought."),
                Q("You give good silence, by the way."),
                N("He gets out. Closes the door. Does not look back.")
            },
            "L1_B6_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L1_B6_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L1_B6_A_CARE", "Take care of yourself.", 1, "L1_END_CARE"),
                Option("L1_B6_B_LIGHT", "That light upstairs is still on.", 0, "L1_END_LIGHT"),
                Option("L1_B6_C_WRITE", "Write back to that student.", 2, "L1_END_WRITE", unlocked: true, requiredOptionId: "L1_B5_B_WORK"),
                Option("L1_B6_D_VERSION", "Back then, this wasn't good enough.", 2, "L1_END_VERSION", unlocked: true, requiredOptionId: "L1_B4_C_MISS")
            },
            -3,
            "L1_END_MISS"));

        blocks.Add(Block("L1_END_CARE", new[] { N("He is already walking. But something in his pace has the shape of someone who heard it.") }, "END"));
        blocks.Add(Block("L1_END_LIGHT", new[] { N("He glances up at it. No comment. He goes inside.") }, "END"));
        blocks.Add(Block("L1_END_WRITE", new[] { N("He stops walking for just a moment. Then continues."), N("He does not acknowledge it out loud. But he stopped.") }, "END"));
        blocks.Add(Block("L1_END_VERSION", new[] { N("He stands at the door for about two seconds. Then he goes in."), N("No reply. Does not look back.") }, "END"));
        blocks.Add(Block("L1_END_MISS", new[] { N("The door closes."), N("The cab sits for a moment, then pulls away.") }, "END"));
    }

    private string GetDanielWorkLine()
    {
        switch (StorySessionState.Tone)
        {
            case "WARM":
                return "The kind of work that makes you feel like you're behind.";
            case "NEUTRAL_WARM":
                return "Good work. Not the kind you see often enough.";
            case "COLD":
                return "Competent.";
            case "NEUTRAL":
            default:
                return "Good work.";
        }
    }

    private void LoadLevel1Data()
    {
        blocks.Add(Block(
            "L1_B1_SETUP",
            new[]
            {
                N("A little after midnight, a man in a dark office jacket gets in."),
                N("His tie is loose. One sleeve is still buttoned, the other is not."),
                Q("Kanda Business Hotel. East entrance."),
                N("He shuts the door, then remembers the receipt before the cab moves."),
                Q("Can you keep the receipt normal? No company name."),
                N("He says it casually. Too casually.")
            },
            "L1_B1_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L1_B1_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L1_B1_WORK", "Bad night at work?", 1, "L1_B1_WORK_REPLY"),
                Option("L1_B1_RECEIPT", "Cash receipt, then.", 0, "L1_B1_RECEIPT_REPLY"),
                Option("L1_B1_RULE", "I follow the meter.", -1, "L1_B1_RULE_REPLY"),
                Option("L1_B1_DEST", "East entrance?", 0, "L1_B1_DEST_REPLY")
            },
            -1,
            "L1_B1_MISS"));

        blocks.Add(Block("L1_B1_WORK_REPLY", new[] { Q("That obvious?"), N("He laughs once, without much air in it."), Q("Yeah. Closing night.") }, "L1_B2_SETUP"));
        blocks.Add(Block("L1_B1_RECEIPT_REPLY", new[] { Q("Thanks."), N("He folds the word small, like he does not want it noticed."), Q("Long event. Longer afterparty.") }, "L1_B2_SETUP"));
        blocks.Add(Block("L1_B1_RULE_REPLY", new[] { Q("Right. Of course."), N("He looks out the window, embarrassed more than annoyed."), Q("Sorry. Habit.") }, "L1_B2_SETUP"));
        blocks.Add(Block("L1_B1_DEST_REPLY", new[] { Q("Yeah. Not the lobby."), N("He rubs at a mark on his wrist where a staff band used to be."), Q("I have seen enough front doors tonight.") }, "L1_B2_SETUP"));
        blocks.Add(Block("L1_B1_MISS", new[] { N("No answer."), N("The man checks the meter, then the window."), Q("East entrance is fine.") }, "L1_B2_SETUP"));

        blocks.Add(Block(
            "L1_B2_SETUP",
            new[]
            {
                N("The cab passes the road toward the Exhibition Center."),
                N("The man notices without turning his head."),
                Q("You work this area often?"),
                N("He taps one finger against a thin stack of folded papers in his lap."),
                Q("There was a girl outside the south doors tonight."),
                Q("White jacket. School bag, maybe. Hard to tell in that light.")
            },
            "L1_B2_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L1_B2_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L1_B2_GIRL", "What about her?", 1, "L1_B2_GIRL_REPLY"),
                Option("L1_B2_CENTER", "At the center?", 1, "L1_B2_CENTER_REPLY"),
                Option("L1_B2_NONE", "People wait outside.", 0, "L1_B2_NONE_REPLY"),
                Option("L1_B2_COLD", "Not my problem.", -1, "L1_B2_COLD_REPLY")
            },
            -1,
            "L1_B2_MISS"));

        blocks.Add(Block("L1_B2_GIRL_REPLY", new[] { Q("Nothing dramatic."), N("He says it quickly, then thinks better of it."), Q("That is what bothered me.") }, "L1_B3_SETUP"));
        blocks.Add(Block("L1_B2_CENTER_REPLY", new[] { Q("South side. Near the service gate."), N("He watches the lights slide over the dashboard."), Q("Not where guests usually stand.") }, "L1_B3_SETUP"));
        blocks.Add(Block("L1_B2_NONE_REPLY", new[] { Q("Sure."), N("He nods, accepting the ordinary version."), Q("People also leave. That part is harder to explain upstairs.") }, "L1_B3_SETUP"));
        blocks.Add(Block("L1_B2_COLD_REPLY", new[] { Q("No. I know."), N("His voice tightens. Not angry. Guarded."), Q("It was mine for about ten minutes, unfortunately.") }, "L1_B3_SETUP"));
        blocks.Add(Block("L1_B2_MISS", new[] { N("The man waits for a reaction and gets none."), Q("Never mind. Bad habit."), N("He refolds the papers in his lap.") }, "L1_B3_SETUP"));

        blocks.Add(Block(
            "L1_B3_SETUP",
            new[]
            {
                N("A message lights his phone. He flips it face down."),
                Q("I was checking badges at the side entrance."),
                Q("People kept asking if one guest had arrived."),
                N("He presses his thumb into the bridge of his nose."),
                Q("Not loudly. That is the thing. Polite people can make a room feel locked.")
            },
            "L1_B3_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L1_B3_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L1_B3_WHO", "Who was asking?", 1, "L1_B3_WHO_REPLY"),
                Option("L1_B3_LOCKED", "Locked how?", 1, "L1_B3_LOCKED_REPLY"),
                Option("L1_B3_WORK", "That was your job.", 0, "L1_B3_WORK_REPLY"),
                Option("L1_B3_DROP", "Let it go.", -1, "L1_B3_DROP_REPLY")
            },
            -1,
            "L1_B3_MISS"));

        blocks.Add(Block("L1_B3_WHO_REPLY", new[] { Q("A man from the sponsor table."), N("He does not say a name."), Q("Charcoal suit. Good watch. Bad smile.") }, "L1_B4_SETUP"));
        blocks.Add(Block("L1_B3_LOCKED_REPLY", new[] { Q("Everyone is free to leave."), N("He looks at the locked doors of a closed bank as the cab passes."), Q("Some rooms just make the exit look rude.") }, "L1_B4_SETUP"));
        blocks.Add(Block("L1_B3_WORK_REPLY", new[] { Q("It was."), N("He accepts it too fast."), Q("Stamp the badge. Smile. Pretend not to notice who is scared of being noticed.") }, "L1_B4_SETUP"));
        blocks.Add(Block("L1_B3_DROP_REPLY", new[] { Q("I tried."), N("His fingers stop moving."), Q("That is why I am still talking about it in a taxi.") }, "L1_B4_SETUP"));
        blocks.Add(Block("L1_B3_MISS", new[] { N("Silence sits between the front and back seats."), Q("Anyway."), N("He looks older for a second.") }, "L1_B4_SETUP"));

        blocks.Add(Block(
            "L1_B4_SETUP",
            new[]
            {
                N("The cab slows near a line of taxis waiting outside a hotel."),
                Q("Do not take the main road, if you can help it."),
                N("He leans slightly forward."),
                Q("Too many lights. Too much glass."),
                N(GetLevel0EchoLine()),
                Q("Take the river road?")
            },
            "L1_B4_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L1_B4_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L1_B4_RIVER", "River road.", 1, "L1_B4_RIVER_REPLY"),
                Option("L1_B4_FAST", "Main road is faster.", 0, "L1_B4_FAST_REPLY"),
                Option("L1_B4_ASK", "Why river?", 1, "L1_B4_ASK_REPLY"),
                Option("L1_B4_NO", "No detours.", -1, "L1_B4_NO_REPLY")
            },
            -1,
            "L1_B4_MISS"));

        blocks.Add(Block("L1_B4_RIVER_REPLY", new[] { Q("Thanks."), N("His shoulders lower a little."), Q("Stupid, right? Picking roads like they change anything.") }, "L1_B5_SETUP"));
        blocks.Add(Block("L1_B4_FAST_REPLY", new[] { Q("Faster is fair."), N("He watches the next red light turn against them."), Q("Everyone wanted faster tonight.") }, "L1_B5_SETUP"));
        blocks.Add(Block("L1_B4_ASK_REPLY", new[] { Q("Less reflection."), N("He gives the answer before he can edit it."), Q("Some faces follow you in glass.") }, "L1_B5_SETUP"));
        blocks.Add(Block("L1_B4_NO_REPLY", new[] { Q("Right."), N("He sits back. The papers in his lap crinkle."), Q("Of course.") }, "L1_B5_SETUP"));
        blocks.Add(Block("L1_B4_MISS", new[] { N("No response. The cab keeps its line."), Q("Main road, then."), N("He says it like accepting a form stamp.") }, "L1_B5_SETUP"));

        blocks.Add(Block(
            "L1_B5_SETUP",
            new[]
            {
                N("The river appears between buildings, black and bright at the same time."),
                Q("Her badge never scanned."),
                N("He says it very quietly."),
                Q("That is all I know for sure."),
                Q("She came near the door. Looked in. Then turned away."),
                N("He looks down at the folded papers."),
                Q("I told them maybe she was late.")
            },
            "L1_B5_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L1_B5_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L1_B5_TRUTH", "You saw her leave.", 1, "L1_B5_TRUTH_REPLY"),
                Option("L1_B5_KIND", "You gave her room.", 1, "L1_B5_KIND_REPLY"),
                Option("L1_B5_LIE", "So you lied.", -1, "L1_B5_LIE_REPLY"),
                Option("L1_B5_SAFE", "Maybe safer that way.", 0, "L1_B5_SAFE_REPLY")
            },
            -1,
            "L1_B5_MISS"));

        blocks.Add(Block("L1_B5_TRUTH_REPLY", new[] { Q("I saw her choose a direction."), N("He looks up at the rearview mirror, then away from it."), Q("Leaving sounds like I knew where she went.") }, "L1_B6_SETUP"));
        blocks.Add(Block("L1_B5_KIND_REPLY", new[] { Q("Maybe."), N("That word gives him less comfort than he hoped."), Q("Or I avoided paperwork and called it kindness.") }, "L1_B6_SETUP"));
        blocks.Add(Block("L1_B5_LIE_REPLY", new[] { Q("Yes."), N("He takes the hit. No defense."), Q("A small one. Those are the easiest to keep.") }, "L1_B6_SETUP"));
        blocks.Add(Block("L1_B5_SAFE_REPLY", new[] { Q("That is what I told myself."), N("He smiles faintly, unhappy with the shape of it."), Q("Very useful phrase.") }, "L1_B6_SETUP"));
        blocks.Add(Block("L1_B5_MISS", new[] { N("The man waits."), Q("I know. It does not make a clean story."), N("He folds the papers once more.") }, "L1_B6_SETUP"));

        blocks.Add(Block(
            "L1_B6_SETUP",
            new[]
            {
                N("The hotel sign comes into view."),
                Q("Someone will ask me tomorrow."),
                Q("Maybe not officially. Just over coffee. In a hallway."),
                N("His hand hovers over the door handle before the car has stopped."),
                Q("If they ask whether I saw her, what would you say?")
            },
            "L1_B6_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L1_B6_RESPONSE",
            System.Array.Empty<string>(),
            new List<PlayerResponseOption>
            {
                Option("L1_B6_PLAIN", "Say what you saw.", 1, "L1_END_PLAIN"),
                Option("L1_B6_LESS", "Say less.", 0, "L1_END_LESS"),
                Option("L1_B6_SELF", "Protect yourself.", -1, "L1_END_SELF"),
                Option("L1_B6_ASK", "Ask why they care.", 1, "L1_END_ASK")
            },
            -2,
            "L1_END_MISS"));

        blocks.Add(Block(
            "L1_END_PLAIN",
            new[]
            {
                Q("What I saw."),
                N("He nods once, like signing something invisible."),
                Q("That she did not come in."),
                Q("That she had time to."),
                N("He pays in cash and steps out under the hotel awning.")
            },
            "END"));

        blocks.Add(Block(
            "L1_END_LESS",
            new[]
            {
                Q("Say less."),
                N("He repeats it like a familiar office skill."),
                Q("I am very good at that."),
                N("He gets out, then pauses before closing the door."),
                Q("Not proud of it.")
            },
            "END"));

        blocks.Add(Block(
            "L1_END_SELF",
            new[]
            {
                Q("That is the smart answer."),
                N("He does not sound relieved."),
                Q("I have been smart all week."),
                N("The door closes softly behind him.")
            },
            "END"));

        blocks.Add(Block(
            "L1_END_ASK",
            new[]
            {
                Q("Ask why they care..."),
                N("For the first time, he almost laughs like he means it."),
                Q("That would ruin a perfectly polite hallway."),
                N("He gets out with the folded papers still in his hand.")
            },
            "END"));

        blocks.Add(Block(
            "L1_END_MISS",
            new[]
            {
                N("The timer runs out."),
                N("The cab stops at the east entrance."),
                Q("Right."),
                Q("I suppose silence is an answer too."),
                N("He leaves the receipt on the seat and steps into the hotel light.")
            },
            "END"));
    }

    private string GetLevel0EchoLine()
    {
        switch (StorySessionState.Tone)
        {
            case "WARM":
                return "For some reason, the river road feels like the kind of choice someone would thank you for.";
            case "COLD":
                return "For some reason, the main road feels easier when nobody has answered you all night.";
            case "NEUTRAL_WARM":
                return "For some reason, the river road feels like it might leave a little more room.";
            case "NEUTRAL":
                return "For some reason, either route feels like a guess.";
            default:
                return "For some reason, the river road feels quieter.";
        }
    }

    private List<PlayerResponseOption> CreateLevel3Block1Options()
    {
        List<PlayerResponseOption> options = new List<PlayerResponseOption>();
        if (HasLevel3WarmTone())
        {
            options.Add(Option("L3_B1_A_SHOW", "Looks like the show went late.", 1, "L3_B1_A_REPLY"));
            options.Add(Option("L3_B1_C_LOST", "You almost lost one of those.", 0, "L3_B1_C_REPLY"));
            options.Add(Option("L3_B1_B_STUDIOS", "Riverside Studios. Got it.", 0, "L3_B1_B_REPLY"));
        }
        else
        {
            options.Add(Option("L3_B1_B_STUDIOS", "Riverside Studios. Got it.", 0, "L3_B1_B_REPLY"));
            options.Add(Option("L3_B1_C_LOST", "You almost lost one of those.", 0, "L3_B1_C_REPLY"));
            options.Add(Option("L3_B1_A_SHOW", "Looks like the show went late.", 1, "L3_B1_A_REPLY"));
        }

        options.Add(Option("L3_B1_SILENCE", "...", -1, "L3_B1_MISS"));
        return options;
    }

    private List<PlayerResponseOption> CreateLevel3Block2Options()
    {
        List<PlayerResponseOption> options = new List<PlayerResponseOption>();
        bool earlyDynamic = HasLevel3WarmTone() || HasLevel3StrongListeningProfile();
        if (earlyDynamic)
        {
            options.Add(Option("L3_B2_D_IMPORTANT", "Someone left you something important.", 2, "L3_B2_D_REPLY", unlocked: true));
        }

        options.Add(Option("L3_B2_A_WROTE", "People wrote things?", 1, "L3_B2_A_REPLY"));
        options.Add(Option("L3_B2_B_STUPID", "Why did you think it was stupid?", 1, "L3_B2_B_REPLY"));
        options.Add(Option("L3_B2_C_STREETS", "Night streets make good photographs.", 0, "L3_B2_C_REPLY"));

        if (!earlyDynamic && HasLevel3MediumListeningProfile())
        {
            options.Add(Option("L3_B2_D_IMPORTANT", "Someone left you something important.", 2, "L3_B2_D_REPLY", unlocked: true));
        }

        options.Add(Option("L3_B2_SILENCE", "...", -1, "L3_B2_MISS"));
        return options;
    }

    private List<PlayerResponseOption> CreateLevel3Block3Options()
    {
        List<PlayerResponseOption> options = new List<PlayerResponseOption>
        {
            Option("L3_B3_A_NOTICED", "You didn't know she was there?", 1, "L3_B3_A_REPLY"),
            Option("L3_B3_B_WAIT", "Why wouldn't she wait?", 1, "L3_B3_B_REPLY")
        };

        if (HasHartwellRecognition())
        {
            options.Add(Option("L3_B3_C_HARTWELL", "Hartwell. The Fine Arts department.", 2, "L3_B3_C_REPLY", keyword: true));
        }

        if (HasDanielRecognitionChain())
        {
            options.Add(Option("L3_B3_D_QUIET", "She mentioned someone had been quiet.", 2, "L3_B3_D_REPLY", keyword: true));
        }

        if (HasLevel3StrongListeningProfile() || HasLevel3WarmTone())
        {
            options.Add(Option("L3_B3_E_AFTER", "Maybe she wanted you to find it after.", 2, "L3_B3_E_REPLY", unlocked: true));
        }

        options.Add(Option("L3_B3_SILENCE", "...", -1, "L3_B3_MISS"));
        return options;
    }

    private List<PlayerResponseOption> CreateLevel3Block4Options()
    {
        List<PlayerResponseOption> options = new List<PlayerResponseOption>
        {
            Option("L3_B4_A_WHAT", "What did it say?", 1, "L3_B4_A_REPLY"),
            Option("L3_B4_B_KEPT", "You kept it.", 0, "L3_B4_B_REPLY"),
            Option("L3_B4_C_MEANT", "If you knew it was hers, you must know what she meant.", 1, "L3_B4_C_REPLY")
        };

        if (HasMargaretLostWords())
        {
            options.Add(Option("L3_B4_D_FIRST", "She'd been trying to say something else first.", 2, "L3_B4_D_REPLY", keyword: true));
        }

        if (HasMargaretEntryChain())
        {
            options.Add(Option("L3_B4_E_ENTRY", "Someone submitted her work for her, didn't they?", 2, "L3_B4_E_REPLY", keyword: true));
        }

        if (HasLevel3StrongListeningProfile())
        {
            options.Add(Option("L3_B4_F_ANSWER", "Maybe she wanted someone to answer without making her ask.", 3, "L3_B4_F_REPLY", unlocked: true, requiredAffection: 7));
        }

        options.Add(Option("L3_B4_SILENCE", "...", -1, "L3_B4_MISS"));
        return options;
    }

    private List<PlayerResponseOption> CreateLevel3Block5Options()
    {
        List<PlayerResponseOption> options = new List<PlayerResponseOption>();

        if (HasLevel3StrongListeningProfile())
        {
            options.Add(Option("L3_B5_D_ROOM", "She was asking whether there was still room for her.", 3, "L3_B5_D_REPLY", unlocked: true, requiredAffection: 7));
        }

        options.Add(Option("L3_B5_A_THEN", "Then what was it?", 1, "L3_B5_A_REPLY"));
        options.Add(Option("L3_B5_B_SELF", "Maybe it was for herself more than for you.", 1, "L3_B5_B_REPLY"));
        options.Add(Option("L3_B5_C_ANSWER", "If it wasn't goodbye, you can still answer.", 2, "L3_B5_C_REPLY"));

        if (!HasLevel3StrongListeningProfile() && HasVeraIncludeQuestion())
        {
            options.Add(Option("L3_B5_D_ROOM", "She was asking whether there was still room for her.", 3, "L3_B5_D_REPLY", unlocked: true, requiredAffection: 7));
        }

        if (HasDanielRecognitionChain() && HasHartwellRecognition())
        {
            options.Add(Option("L3_B5_E_DANIEL", "She didn't go to Daniel.", 2, "L3_B5_E_REPLY", keyword: true));
        }

        if (HasLevel3StrongListeningProfile() || HasMargaretLostWords())
        {
            options.Add(Option("L3_B5_F_BLANK", "Maybe the blank space was the part she wanted answered.", 3, "L3_B5_F_REPLY", unlocked: true, requiredOptionId: "L3_B4_D_FIRST"));
        }

        options.Add(Option("L3_B5_SILENCE", "...", -2, "L3_B5_MISS"));
        return options;
    }

    private List<PlayerResponseOption> CreateLevel3Block6Options()
    {
        List<PlayerResponseOption> options = new List<PlayerResponseOption>();
        if (HasLevel3StrongListeningProfile())
        {
            options.Add(Option("L3_B6_B_TWICE", "Don't make her ask twice.", 2, "L3_END_WARMEST", unlocked: true, requiredAffection: 7));
        }

        options.Add(Option("L3_B6_A_WRITE", "Write first. Then show up.", 1, "L3_END_REPLY"));
        options.Add(Option("L3_B6_C_WAIT", "If you wait, that's an answer too.", 1, "L3_END_PUSHED"));
        options.Add(Option("L3_B6_D_SAW", "Maybe just tell her you saw it.", 0, "L3_END_GENTLE"));
        options.Add(Option("L3_B6_E_KNOW", "You already know what to do.", 0, "L3_END_OPEN"));
        options.Add(Option("L3_B6_SILENCE", "...", -1, "L3_END_UNCERTAIN"));
        return options;
    }

    private string[] GetLevel3Block1CReplyLines()
    {
        List<string> lines = new List<string>
        {
            N("Owen glances at the paper tube nearest the door."),
            Q("Story of my life. Almost losing things, then pretending that was the plan."),
            N("He smiles at that, then stops smiling a little too quickly.")
        };

        if (HasVeraIncludeQuestion())
        {
            lines.Add(Q("Someone asked me once if almost counted. I said probably. Terrible answer."));
        }

        return lines.ToArray();
    }

    private string[] GetLevel3RadioRockSetupLines()
    {
        return new[]
        {
            N("The cab rolls under a string of orange streetlights. Owen taps two fingers against the paper tube, then stops when he notices he's doing it."),
            Q("Can I ask for a weirdly specific favour?"),
            N("He glances toward the dash, already half-embarrassed by the request."),
            Q("Could you put on something with guitars? Rock, maybe."),
            Q("I need something loud enough to stop me thinking for five seconds."),
            N("(He wants something loud and guitar-heavy. Try switching to rock.)")
        };
    }

    private string[] GetLevel3RadioRockContinueLines()
    {
        return new[]
        {
            N("The radio clicks through static and lands on a low, grainy rock track."),
            N("Owen leans back for the first time since getting in."),
            Q("Yeah. That. Perfect."),
            N("For a moment, the silence in the cab stops feeling like something waiting to happen.")
        };
    }

    private string[] GetLevel3RadioClassicalSetupLines()
    {
        return new[]
        {
            N("They stop at a light with no cross traffic. Owen watches the color slide across the window, then exhales."),
            Q("Can I ask for a weirdly specific favour?"),
            N("He smiles at himself, like he knows how unreasonable he's about to sound."),
            Q("Could you put on something classical? Piano, if you've got it."),
            Q("I think I need the sort of music that makes everything feel arranged on purpose for five seconds."),
            N("(He wants something classical and ordered. Try switching to the waltz.)")
        };
    }

    private string[] GetLevel3RadioClassicalContinueLines()
    {
        return new[]
        {
            N("The radio slips through static and settles on a bright piano waltz."),
            N("Owen goes quiet long enough to hear the first phrase all the way through."),
            Q("Yeah. That one."),
            N("The cab doesn't get less strange. It just starts to feel more ordered.")
        };
    }

    private string[] GetLevel3Block3SetupLines(string musicLeadLine)
    {
        return new[]
        {
            musicLeadLine,
            N("A long road along the back of the shopping district. Most signs are off. A few are still glowing blue-white."),
            N("Owen takes his phone out, checks it, sees no new messages, puts it away. Then immediately takes it out again and locks the screen."),
            Q("A girl from school came by tonight. I didn't see her during the show."),
            N("He says it quickly. Too quickly to be casual."),
            Q("Or maybe I did and didn't notice. Which is worse, probably."),
            N("He rubs his thumb along the edge of the phone."),
            Q("Vera."),
            N("The name lands plainly. No dramatic sting. Just the missing piece being named because Owen has no reason not to."),
            Q("She's in Fine Arts. Hartwell. Same year as me."),
            N("Owen looks out the window."),
            Q("She left before I found the note.")
        };
    }

    private string[] GetLevel3Block4AReplyLines()
    {
        List<string> lines = new List<string>
        {
            N("Owen's hand stays in his pocket."),
            Q("Not exactly something you can read out loud in a cab."),
            N("He tries to smile. It doesn't quite work."),
            Q("Not because it was private. Because reading it out loud would make it smaller.")
        };

        if (HasLevel3MediumListeningProfile())
        {
            lines.Add(Q("It sounded like someone deciding to stop apologizing for taking up space."));
        }

        return lines.ToArray();
    }

    private string[] GetLevel3Block4EReplyLines()
    {
        List<string> lines = new List<string>
        {
            N("Owen's expression changes -- surprise, then recognition."),
            Q("I heard that after."),
            N("He looks down at the portfolio case."),
            Q("The entry list had her name on it. She told everyone she hadn't decided yet. But her name was there.")
        };

        if (StorySessionState.HasSelectedOption("L2_B4_G_COST"))
        {
            lines.Add(Q("Margaret must have taken a hit for that. Maybe not publicly. But rooms like that keep score."));
        }

        return lines.ToArray();
    }

    private string[] GetLevel3Block4FReplyLines()
    {
        List<string> lines = new List<string>
        {
            N("Owen's hand closes around the note in his pocket."),
            Q("That's unfairly accurate."),
            N("A beat."),
            Q("She does that. Stands near a question and waits to see if anyone notices the shape of it.")
        };

        if (HasVeraIncludeQuestion())
        {
            lines.Add(Q("She asked me something like that once. Not the words. The shape."));
        }

        return lines.ToArray();
    }

    private string[] GetLevel3Block5AReplyLines()
    {
        List<string> lines = new List<string>
        {
            N("Owen exhales."),
            Q("Maybe a marker. Like when you leave a light on in a room so you can come back to it.")
        };

        lines.Add(Q(GetLevel3ToneMeaningLine()));
        return lines.ToArray();
    }

    private string[] GetLevel3Block5BReplyLines()
    {
        List<string> lines = new List<string>
        {
            N("Owen looks at the note."),
            Q("Yeah. Maybe."),
            N("He smiles faintly."),
            Q("That would be very Vera. Give someone a message so she doesn't have to say it to a mirror.")
        };

        if (StorySessionState.HasSelectedOption("L3_B3_E_AFTER"))
        {
            lines.Add(Q("But she still put it where I would find it. So not just for herself."));
        }

        return lines.ToArray();
    }

    private string[] GetLevel3Block6SetupLines()
    {
        List<string> lines = new List<string>
        {
            N("Riverside Studios. The old warehouses sit low along the canal. A single upstairs window is lit in one of them."),
            N("The cab stops. Owen gathers the paper tubes first, then forgets the portfolio, then remembers it. He laughs under his breath."),
            Q("Right. This is me."),
            N("He does not open the door yet. The note is back in his coat pocket. His phone is in his other hand.")
        };

        if (HasDanielPrompted())
        {
            lines.Add(N("His phone vibrates once. He glances at it. The screen is not shown clearly. His expression changes only slightly -- recognition mixed with disbelief. He does not explain."));
        }

        if (HasMargaretPromised())
        {
            lines.Add(Q("Someone came by the next day, actually. Margaret. She didn't stay long."));
            lines.Add(N("A beat."));
            lines.Add(Q("She said Vera's name like it cost her something and helped her anyway."));
        }
        else if (HasMargaretQuietDeparture())
        {
            lines.Add(Q("I keep thinking there were people around her who knew more than they said."));
            lines.Add(N("He looks out at the warehouse window."));
            lines.Add(Q("Maybe that's everyone."));
        }

        lines.Add(Q("I think I'm going to write back."));
        lines.Add(N("He smiles faintly, nervous."));
        lines.Add(Q("Or call. Calling feels insane. Writing feels cowardly. Showing up feels like something from a movie where people have better lighting."));
        lines.Add(N("He looks toward the mirror."));
        lines.Add(Q("What would you do?"));
        return lines.ToArray();
    }

    private string GetLevel3ToneMeaningLine()
    {
        switch (StorySessionState.Tone)
        {
            case "WARM":
                return "She wrote like someone who had decided to try, but didn't trust the decision enough to announce it.";
            case "NEUTRAL_WARM":
                return "She wrote like someone who had almost decided. Which, for Vera, might be the same as deciding.";
            case "COLD":
                return "She wrote like someone leaving before anyone could stop her. Or ask her to stay.";
            case "NEUTRAL":
            default:
                return "She wrote like someone who came by. That's all I know for sure.";
        }
    }

    private bool HasLevel3WarmTone()
    {
        return string.Equals(StorySessionState.Tone, "WARM", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(StorySessionState.Tone, "NEUTRAL_WARM", StringComparison.OrdinalIgnoreCase);
    }

    private int GetLevel3ListeningScore()
    {
        int score = 0;
        switch (StorySessionState.Tone)
        {
            case "WARM":
                score += 3;
                break;
            case "NEUTRAL_WARM":
                score += 2;
                break;
            case "NEUTRAL":
                score += 1;
                break;
        }

        score += CountSelectedOptions(
            "L0_B3_B1",
            "L1_B2_B_MADE",
            "L1_B3_B_TEACH",
            "L1_B3_C_LIKE",
            "L1_B4_D_PROTECT",
            "L1_B5_B_WORK",
            "L1_B6_C_WRITE",
            "L2_B2_A_STUDENT",
            "L2_B3_D_SOMEONE",
            "L2_B4_D_KNOWN",
            "L2_B4_E_HARTWELL",
            "L2_B4_G_COST",
            "L2_B5_E_ANSWER",
            "L2_B6_A_TELL");

        if (!StorySessionState.WasBlockMissed("L1_B5_RESPONSE"))
        {
            score++;
        }

        if (!StorySessionState.WasBlockMissed("L2_B5_RESPONSE"))
        {
            score++;
        }

        return score;
    }

    private bool HasLevel3MediumListeningProfile()
    {
        return GetLevel3ListeningScore() >= 4;
    }

    private bool HasLevel3StrongListeningProfile()
    {
        return GetLevel3ListeningScore() >= 7;
    }

    private bool HasVeraIncludeQuestion()
    {
        return StorySessionState.HasSelectedOption("L0_B3_B1");
    }

    private bool HasHartwellRecognition()
    {
        return HasAnySelectedOption("L1_B4_D_PROTECT", "L2_B4_E_HARTWELL", "L2_B6_D_HARTWELL");
    }

    private bool HasDanielRecognitionChain()
    {
        return HasAnySelectedOption("L1_B5_B_WORK", "L1_B5_D_ARETHEY", "L1_B6_C_WRITE", "L1_B4_D_PROTECT");
    }

    private bool HasDanielPrompted()
    {
        return HasAnySelectedOption("L1_B5_C_WAITING", "L1_B5_B_WORK", "L1_B6_C_WRITE");
    }

    private bool HasMargaretLostWords()
    {
        return HasAnySelectedOption("L2_B3_A_UNSAID", "L2_B3_D_SOMEONE", "L2_B5_E_ANSWER");
    }

    private bool HasMargaretEntryChain()
    {
        return HasAnySelectedOption("L2_B2_A_STUDENT", "L2_B4_E_HARTWELL", "L2_B4_F_TONIGHT", "L2_B4_G_COST");
    }

    private bool HasMargaretPromised()
    {
        return HasAnySelectedOption("L2_B5_B_STILL", "L2_B5_D_MEANT", "L2_B6_A_TELL");
    }

    private bool HasMargaretQuietDeparture()
    {
        return HasAnySelectedOption("L2_B6_B_REST", "L2_B6_C_GOODNIGHT");
    }

    private static bool HasAnySelectedOption(params string[] optionIds)
    {
        for (int i = 0; i < optionIds.Length; i++)
        {
            if (StorySessionState.HasSelectedOption(optionIds[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountSelectedOptions(params string[] optionIds)
    {
        int count = 0;
        for (int i = 0; i < optionIds.Length; i++)
        {
            if (StorySessionState.HasSelectedOption(optionIds[i]))
            {
                count++;
            }
        }

        return count;
    }

    private static NpcSpeakingBlock Block(string blockId, params string[] lines)
    {
        return new NpcSpeakingBlock(
            blockId,
            lines,
            false,
            new List<PlayerResponseOption>());
    }

    private static NpcSpeakingBlock Block(string blockId, string[] lines, string nextBlockId)
    {
        return new NpcSpeakingBlock(
            blockId,
            lines,
            false,
            new List<PlayerResponseOption>(),
            nextBlockId);
    }

    private static NpcSpeakingBlock Block(string blockId, string line1, string line2, string line3, string nextBlockId)
    {
        return Block(blockId, new[] { line1, line2, line3 }, nextBlockId);
    }

    private static NpcSpeakingBlock Block(string blockId, string line1, string line2, string line3, string line4, string nextBlockId)
    {
        return Block(blockId, new[] { line1, line2, line3, line4 }, nextBlockId);
    }

    private static NpcSpeakingBlock Block(string blockId, string line1, string line2, string line3, string line4, string line5, string line6, string nextBlockId)
    {
        return Block(blockId, new[] { line1, line2, line3, line4, line5, line6 }, nextBlockId);
    }

    private static NpcSpeakingBlock ResponseBlock(
        string blockId,
        string[] lines,
        List<PlayerResponseOption> options,
        int noResponseAffectionDelta,
        string noResponseNextBlockId,
        float responseWindowSecondsOverride = -1f)
    {
        string defaultNextBlockId = options.Count > 0 ? options[0].nextBlockId : null;
        return new NpcSpeakingBlock(
            blockId,
            lines,
            true,
            options,
            defaultNextBlockId,
            noResponseAffectionDelta,
            noResponseNextBlockId,
            responseWindowSecondsOverride);
    }

    private static PlayerResponseOption Option(string optionId, string text, int affectionDelta, string nextBlockId)
    {
        return new PlayerResponseOption(optionId, text, affectionDelta, nextBlockId);
    }

    private static PlayerResponseOption Option(
        string optionId,
        string text,
        int affectionDelta,
        string nextBlockId,
        bool keyword = false,
        bool unlocked = false,
        string requiredOptionId = null,
        int requiredAffection = 0)
    {
        return new PlayerResponseOption(
            optionId,
            text,
            affectionDelta,
            nextBlockId,
            keyword,
            unlocked,
            requiredOptionId,
            requiredAffection);
    }

    private static string N(string text)
    {
        return "(" + text + ")";
    }

    private static string Q(string text)
    {
        return "\"" + text + "\"";
    }
}
