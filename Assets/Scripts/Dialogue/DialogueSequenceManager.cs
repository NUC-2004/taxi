using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class DialogueSequenceManager : MonoBehaviour
{
    [Header("Sequence")]
    public float secondsPerNpcFragment = 1.7f;
    public float extraNpcFragmentHoldSeconds = 0.5f;
    public float typewriterCharacterInterval = 0.035f;
    public float responseWindowSeconds = 6f;
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

    public event Action<DialoguePhase> PhaseChanged;
    public event Action<NpcSpeakingBlock, int, int> BlockChanged;
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
        CurrentResponseDurationSeconds = 0f;
        affection = Mathf.Clamp(initialAffection, minAffection, maxAffection);
        AffectionChanged?.Invoke(affection, minAffection, maxAffection);
        BuildBlockLookup();
        sequenceRoutine = StartCoroutine(RunSequence());
    }

    public void ChooseResponse(PlayerResponseOption option)
    {
        if (CurrentPhase != DialoguePhase.PlayerResponse || !waitingForResponse || option == null)
        {
            return;
        }

        if (IsOptionLocked(option))
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
            yield return new WaitForSeconds(secondsPerNpcFragment + extraNpcFragmentHoldSeconds);
            yield break;
        }

        for (int i = 0; i < fragments.Length; i++)
        {
            yield return PlayTypewriterFragment(fragments[i]);
            yield return new WaitForSeconds(secondsPerNpcFragment + extraNpcFragmentHoldSeconds);
        }
    }

    private IEnumerator PlayTypewriterFragment(string fragment)
    {
        if (string.IsNullOrEmpty(fragment))
        {
            NpcTextChanged?.Invoke(string.Empty);
            yield break;
        }

        for (int i = 1; i <= fragment.Length; i++)
        {
            NpcTextChanged?.Invoke(fragment.Substring(0, i));

            if (i < fragment.Length)
            {
                yield return new WaitForSeconds(typewriterCharacterInterval);
            }
        }
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

    private void ApplyAffectionDelta(int delta)
    {
        affection = Mathf.Clamp(affection + delta, minAffection, maxAffection);
        AffectionChanged?.Invoke(affection, minAffection, maxAffection);

        if (delta < 0 &&
            !sequenceFailed &&
            string.Equals(loadedSequenceId, "Level1", StringComparison.OrdinalIgnoreCase) &&
            affection <= minAffection)
        {
            TriggerSequenceFailure();
        }
    }

    private void TriggerSequenceFailure()
    {
        sequenceFailed = true;
        waitingForResponse = false;
        pendingNextBlockId = null;
        CurrentResponseDurationSeconds = 0f;
        ResponseTimerChanged?.Invoke(0f, 0f);
        NpcTextChanged?.Invoke("(Seems the passenger does not want to talk anymore.)");
        SetPhase(DialoguePhase.Failed);
        SequenceFailed?.Invoke();
    }

    private void SetPhase(DialoguePhase phase)
    {
        CurrentPhase = phase;
        PhaseChanged?.Invoke(phase);
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
        string requiredOptionId = null)
    {
        return new PlayerResponseOption(
            optionId,
            text,
            affectionDelta,
            nextBlockId,
            keyword,
            unlocked,
            requiredOptionId);
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
