using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public sealed class DialogueSequenceManager : MonoBehaviour
{
    [Header("Sequence")]
    [SerializeField] private float secondsPerNpcFragment = 1.7f;
    [SerializeField] private float extraNpcFragmentHoldSeconds = 0.5f;
    [SerializeField] private float typewriterCharacterInterval = 0.035f;
    [SerializeField] private float responseWindowSeconds = 4f;
    [SerializeField] private float betweenBlocksDelaySeconds = 0.45f;
    [SerializeField] private int defaultNoResponseAffectionDelta = -12;

    [Header("Affection")]
    [SerializeField] private int initialAffection = 60;
    [SerializeField] private int minAffection = 0;
    [SerializeField] private int maxAffection = 100;

    private readonly List<NpcSpeakingBlock> blocks = new List<NpcSpeakingBlock>();
    private readonly Dictionary<string, NpcSpeakingBlock> blockLookup = new Dictionary<string, NpcSpeakingBlock>();
    private Coroutine sequenceRoutine;
    private bool waitingForResponse;
    private int currentBlockIndex = -1;
    private int affection;
    private string pendingNextBlockId;
    private NpcSpeakingBlock activeBlock;

    public event Action<DialoguePhase> PhaseChanged;
    public event Action<NpcSpeakingBlock, int, int> BlockChanged;
    public event Action<string> NpcTextChanged;
    public event Action<IReadOnlyList<PlayerResponseOption>> ResponseStarted;
    public event Action<float, float> ResponseTimerChanged;
    public event Action<int, int, int> AffectionChanged;
    public event Action<ResponseResult> ResponseResolved;
    public event Action SequenceCompleted;

    public DialoguePhase CurrentPhase { get; private set; } = DialoguePhase.None;
    public int Affection => affection;
    public int MinAffection => minAffection;
    public int MaxAffection => maxAffection;
    public float ResponseWindowSeconds => responseWindowSeconds;

    private void Start()
    {
        if (blocks.Count == 0)
        {
            LoadLevel0PlaceholderData();
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

        waitingForResponse = false;
        if (activeBlock != null)
        {
            StorySessionState.RecordChoice(activeBlock.blockId, option.optionId);
        }
        pendingNextBlockId = option.nextBlockId;
        ApplyAffectionDelta(option.affectionDelta);
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

            if (block.allowsPlayerResponse && block.responseOptions != null && block.responseOptions.Count > 0)
            {
                yield return RunResponseWindow(block);
            }

            yield return new WaitForSeconds(betweenBlocksDelaySeconds);
            block = ResolveNextBlock(block, pendingNextBlockId);
        }

        if (safetyCount >= 200)
        {
            Debug.LogWarning("Dialogue stopped after 200 blocks. Check for an accidental loop in Next Block data.");
        }

        SetPhase(DialoguePhase.Complete);
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
        ResponseStarted?.Invoke(block.responseOptions);

        float remaining = responseWindowSeconds;
        while (waitingForResponse && remaining > 0f)
        {
            ResponseTimerChanged?.Invoke(remaining, responseWindowSeconds);
            remaining -= Time.deltaTime;
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
            ResponseResolved?.Invoke(new ResponseResult(false, null, delta, pendingNextBlockId));
        }

        ResponseTimerChanged?.Invoke(0f, responseWindowSeconds);
    }

    private void ApplyAffectionDelta(int delta)
    {
        affection = Mathf.Clamp(affection + delta, minAffection, maxAffection);
        AffectionChanged?.Invoke(affection, minAffection, maxAffection);
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

    private void LoadLevel0PlaceholderData()
    {
        blocks.Add(Block(
            "L0_OPEN",
            new[]
            {
                "Sorry. Are you still taking fares?",
                "Thank you.",
                "One more thing before I tell you where to go.",
                "When I stop talking, answer me.",
                "Use the keys if you like. Click if you don't. Just don't leave me hanging."
            },
            "L0_FIRST_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L0_FIRST_RESPONSE",
            new[]
            {
                "Good.",
                "At least now I know this won't be one of those rides where I do all the work."
            },
            new List<PlayerResponseOption>
            {
                Option("L0_OPEN_WARM", "I'm listening.", 2, "L0_OPEN_WARM_REPLY"),
                Option("L0_OPEN_WRY", "Depends what kind of work this is.", 1, "L0_OPEN_WRY_REPLY"),
                Option("L0_OPEN_FLAT", "Just tell me the destination.", 0, "L0_OPEN_FLAT_REPLY"),
                Option("L0_OPEN_GUARD", "You're starting strangely.", -1, "L0_OPEN_GUARD_REPLY")
            },
            -1,
            "L0_OPEN_MISS"));

        blocks.Add(Block("L0_OPEN_WARM_REPLY", new[] { "That helps.", "You say that like you mean it." }, "L0_DEST_SETUP"));
        blocks.Add(Block("L0_OPEN_WRY_REPLY", new[] { "Fair question.", "Let's call it emotional meter running." }, "L0_DEST_SETUP"));
        blocks.Add(Block("L0_OPEN_FLAT_REPLY", new[] { "You really are a driver first.", "All right. Keep that tone if you want." }, "L0_DEST_SETUP"));
        blocks.Add(Block("L0_OPEN_GUARD_REPLY", new[] { "I know.", "It has been that kind of night." }, "L0_DEST_SETUP"));
        blocks.Add(Block("L0_OPEN_MISS", new[] { "Right. Silence first.", "I should have expected that from a stranger." }, "L0_DEST_SETUP"));

        blocks.Add(Block(
            "L0_DEST_SETUP",
            new[]
            {
                "I told the doorman Crescent Hotel, and then I walked past the hotel entrance anyway.",
                "I told myself I was only buying time.",
                "Now I am in your cab doing exactly the same thing."
            },
            "L0_DEST_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L0_DEST_RESPONSE",
            new[]
            {
                "Say something useful."
            },
            new List<PlayerResponseOption>
            {
                Option("L0_DEST_ASK", "Then where do you actually want to go?", 1, "L0_DEST_ASK_REPLY"),
                Option("L0_DEST_SOFT", "You don't have to decide this second.", 2, "L0_DEST_SOFT_REPLY"),
                Option("L0_DEST_SHARP", "If you're delaying, you're probably already late.", -1, "L0_DEST_SHARP_REPLY"),
                Option("L0_DEST_CURIOUS", "Who were you supposed to meet there?", 1, "L0_DEST_CURIOUS_REPLY")
            },
            -1,
            "L0_DEST_MISS"));

        blocks.Add(Block("L0_DEST_ASK_REPLY", new[] { "I know the names of the places.", "I don't know which one still belongs to me." }, "L0_NIGHT_CONTEXT"));
        blocks.Add(Block("L0_DEST_SOFT_REPLY", new[] { "That is exactly the problem.", "If no one makes me decide, I can stay like this forever." }, "L0_NIGHT_CONTEXT"));
        blocks.Add(Block("L0_DEST_SHARP_REPLY", new[] { "Late is survivable.", "Wrong is more expensive." }, "L0_NIGHT_CONTEXT"));
        blocks.Add(Block("L0_DEST_CURIOUS_REPLY", new[] { "Someone respectable.", "Which is not the same thing as someone kind." }, "L0_NIGHT_CONTEXT"));
        blocks.Add(Block("L0_DEST_MISS", new[] { "You do that well.", "Leave a space and make the other person fill it." }, "L0_NIGHT_CONTEXT"));

        blocks.Add(Block(
            "L0_NIGHT_CONTEXT",
            new[]
            {
                "Tonight was all chandeliers and polished glass and people speaking softly so they could lie beautifully.",
                "Everyone there had a plan for me.",
                "None of them asked whether I liked it."
            },
            "L0_NIGHT_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L0_NIGHT_RESPONSE",
            new[]
            {
                "Go on. What do you think I should have said to them?"
            },
            new List<PlayerResponseOption>
            {
                Option("L0_NIGHT_LEAVE", "You could have walked out earlier.", 1, "L0_NIGHT_LEAVE_REPLY"),
                Option("L0_NIGHT_ENDURE", "Sometimes you smile, nod, and leave later.", 0, "L0_NIGHT_ENDURE_REPLY"),
                Option("L0_NIGHT_REFUSE", "You say no plainly once and let them deal with it.", 2, "L0_NIGHT_REFUSE_REPLY"),
                Option("L0_NIGHT_DEFLECT", "Depends how dangerous those plans were.", 1, "L0_NIGHT_DEFLECT_REPLY")
            },
            -1,
            "L0_NIGHT_MISS"));

        blocks.Add(Block("L0_NIGHT_LEAVE_REPLY", new[] { "Yes.", "I kept almost doing exactly that." }, "L0_ROUTE_SETUP"));
        blocks.Add(Block("L0_NIGHT_ENDURE_REPLY", new[] { "That answer sounds practiced.", "I am not sure if that comforts me or worries me." }, "L0_ROUTE_SETUP"));
        blocks.Add(Block("L0_NIGHT_REFUSE_REPLY", new[] { "Plainly.", "That word sounds bigger inside a car this quiet." }, "L0_ROUTE_SETUP"));
        blocks.Add(Block("L0_NIGHT_DEFLECT_REPLY", new[] { "Not dangerous in the dramatic sense.", "Only in the ordinary sense. The kind that lasts years." }, "L0_ROUTE_SETUP"));
        blocks.Add(Block("L0_NIGHT_MISS", new[] { "No answer again.", "Maybe you know there isn't a clean one." }, "L0_ROUTE_SETUP"));

        blocks.Add(Block(
            "L0_ROUTE_SETUP",
            new[]
            {
                "Take the long river road for now.",
                "The bright one, not the tunnel.",
                "I don't want the city closing around me just yet."
            },
            "L0_ROUTE_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L0_ROUTE_RESPONSE",
            new[]
            {
                "Would you have picked the same road?"
            },
            new List<PlayerResponseOption>
            {
                Option("L0_ROUTE_BRIGHT", "Bright roads make it easier to keep lying to yourself.", 0, "L0_ROUTE_BRIGHT_REPLY"),
                Option("L0_ROUTE_RIVER", "River road. More time to think.", 2, "L0_ROUTE_RIVER_REPLY"),
                Option("L0_ROUTE_TUNNEL", "Tunnel. Decide faster.", -1, "L0_ROUTE_TUNNEL_REPLY"),
                Option("L0_ROUTE_ASKWHY", "Why not the tunnel?", 1, "L0_ROUTE_ASKWHY_REPLY")
            },
            -1,
            "L0_ROUTE_MISS"));

        blocks.Add(Block("L0_ROUTE_BRIGHT_REPLY", new[] { "Cruel.", "Not inaccurate." }, "L0_NAME_SETUP"));
        blocks.Add(Block("L0_ROUTE_RIVER_REPLY", new[] { "That was my thought too.", "Which may be why I still haven't fixed anything." }, "L0_NAME_SETUP"));
        blocks.Add(Block("L0_ROUTE_TUNNEL_REPLY", new[] { "You and my mother would get along beautifully.", "Everything immediate. Everything efficient." }, "L0_NAME_SETUP"));
        blocks.Add(Block("L0_ROUTE_ASKWHY_REPLY", new[] { "Because in a tunnel all you can do is keep going forward.", "Tonight I wanted at least one lane where I could still imagine turning." }, "L0_NAME_SETUP"));
        blocks.Add(Block("L0_ROUTE_MISS", new[] { "You let me keep the road.", "Interesting." }, "L0_NAME_SETUP"));

        blocks.Add(Block(
            "L0_NAME_SETUP",
            new[]
            {
                "His name is Daichi.",
                "That is probably enough to tell you the shape of the evening.",
                "Not enough to tell you whether I was supposed to marry him, thank him, or apologize to him."
            },
            "L0_NAME_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L0_NAME_RESPONSE",
            new[]
            {
                "Pick one."
            },
            new List<PlayerResponseOption>
            {
                Option("L0_NAME_MARRY", "Marry him.", -1, "L0_NAME_MARRY_REPLY"),
                Option("L0_NAME_APOLOGIZE", "Apologize to him.", 0, "L0_NAME_APOLOGIZE_REPLY"),
                Option("L0_NAME_THANK", "Thank him.", 0, "L0_NAME_THANK_REPLY"),
                Option("L0_NAME_NONE", "None of those felt like yours.", 2, "L0_NAME_NONE_REPLY")
            },
            -1,
            "L0_NAME_MISS"));

        blocks.Add(Block("L0_NAME_MARRY_REPLY", new[] { "That was certainly one version of the evening.", "The expensive version." }, "L0_DECISION_SETUP"));
        blocks.Add(Block("L0_NAME_APOLOGIZE_REPLY", new[] { "There was a lot of apologizing in the room.", "Mostly arranged in advance." }, "L0_DECISION_SETUP"));
        blocks.Add(Block("L0_NAME_THANK_REPLY", new[] { "He prefers gratitude to honesty.", "It photographs better." }, "L0_DECISION_SETUP"));
        blocks.Add(Block("L0_NAME_NONE_REPLY", new[] { "There you are.", "You were paying attention after all." }, "L0_DECISION_SETUP"));
        blocks.Add(Block("L0_NAME_MISS", new[] { "You declined to guess.", "Possibly wise." }, "L0_DECISION_SETUP"));

        blocks.Add(Block(
            "L0_DECISION_SETUP",
            new[]
            {
                "I kept thinking that if one person answered me plainly tonight, I might finally choose something.",
                "Not choose perfectly.",
                "Just choose."
            },
            "L0_DECISION_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L0_DECISION_RESPONSE",
            new[]
            {
                "So answer plainly."
            },
            new List<PlayerResponseOption>
            {
                Option("L0_DECISION_GO", "Go where you want, not where you were presented.", 2, "L0_DECISION_GO_REPLY"),
                Option("L0_DECISION_BACK", "Turn back if you are not ready to lose anything.", 0, "L0_DECISION_BACK_REPLY"),
                Option("L0_DECISION_TELL", "At least tell him the truth once.", 1, "L0_DECISION_TELL_REPLY"),
                Option("L0_DECISION_DRIVER", "Drivers are bad substitutes for decisions.", -1, "L0_DECISION_DRIVER_REPLY")
            },
            -2,
            "L0_DECISION_MISS"));

        blocks.Add(Block("L0_DECISION_GO_REPLY", new[] { "That sounds simple enough to be dangerous.", "I like it." }, "L0_FINAL_APPROACH"));
        blocks.Add(Block("L0_DECISION_BACK_REPLY", new[] { "That is gentler than what I deserve.", "Maybe too gentle." }, "L0_FINAL_APPROACH"));
        blocks.Add(Block("L0_DECISION_TELL_REPLY", new[] { "Truth first.", "A terrifyingly adult suggestion." }, "L0_FINAL_APPROACH"));
        blocks.Add(Block("L0_DECISION_DRIVER_REPLY", new[] { "No.", "But sometimes strangers make better witnesses than friends." }, "L0_FINAL_APPROACH"));
        blocks.Add(Block("L0_DECISION_MISS", new[] { "There it is again.", "The moment where I could have leaned on you and you vanished." }, "L0_FINAL_APPROACH"));

        blocks.Add(Block(
            "L0_FINAL_APPROACH",
            new[]
            {
                "All right.",
                "Do not take me to Crescent Hotel.",
                "Take me to Shin-Ori Station instead.",
                "If I miss the last express, maybe I was never serious.",
                "If I make it..."
            },
            "L0_FINAL_RESPONSE"));

        blocks.Add(ResponseBlock(
            "L0_FINAL_RESPONSE",
            new[]
            {
                "Finish that thought for me."
            },
            new List<PlayerResponseOption>
            {
                Option("L0_FINAL_BEGIN", "If you make it, then begin.", 3, "L0_END_BEGIN"),
                Option("L0_FINAL_BREATHE", "If you make it, then at least you chose.", 2, "L0_END_BREATHE"),
                Option("L0_FINAL_CALL", "If you make it, call him and speak clearly.", 1, "L0_END_CALL"),
                Option("L0_FINAL_COLD", "If you make it, don't romanticize it.", -1, "L0_END_COLD")
            },
            -2,
            "L0_END_MISS"));

        blocks.Add(Block(
            "L0_END_BEGIN",
            new[]
            {
                "Begin.",
                "You say that like people are allowed to do it more than once.",
                "Maybe they are.",
                "Remember you said it."
            },
            "L0_END_COMMON"));

        blocks.Add(Block(
            "L0_END_BREATHE",
            new[]
            {
                "Chosen is enough for one night.",
                "That sounds smaller than courage.",
                "More believable too."
            },
            "L0_END_COMMON"));

        blocks.Add(Block(
            "L0_END_CALL",
            new[]
            {
                "You really want everything tied up cleanly.",
                "I envy that.",
                "Still... perhaps a clear goodbye would count for something."
            },
            "L0_END_COMMON"));

        blocks.Add(Block(
            "L0_END_COLD",
            new[]
            {
                "No.",
                "You are right about that.",
                "A station is still just a station.",
                "The hard part is what I do after I arrive."
            },
            "L0_END_COMMON"));

        blocks.Add(Block(
            "L0_END_MISS",
            new[]
            {
                "You had one last chance to answer.",
                "I will remember that too."
            },
            "L0_END_COMMON"));

        blocks.Add(Block(
            "L0_END_COMMON",
            new[]
            {
                "There. The sign for the station is already in view.",
                "Funny.",
                "At the start of this ride I was making sure you understood the rules.",
                "Now I think I was only making sure you would answer me at all.",
                "Thank you... or no thank you.",
                "Either way, do not forget this night as quickly as you forget the others."
            },
            "END"));
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
        string noResponseNextBlockId)
    {
        string defaultNextBlockId = options.Count > 0 ? options[0].nextBlockId : null;
        return new NpcSpeakingBlock(
            blockId,
            lines,
            true,
            options,
            defaultNextBlockId,
            noResponseAffectionDelta,
            noResponseNextBlockId);
    }

    private static PlayerResponseOption Option(string optionId, string text, int affectionDelta, string nextBlockId)
    {
        return new PlayerResponseOption(optionId, text, affectionDelta, nextBlockId);
    }
}
