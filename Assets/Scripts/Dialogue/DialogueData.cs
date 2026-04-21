using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public enum DialoguePhase
{
    None,
    NpcSpeaking,
    PlayerResponse,
    Complete
}

[Serializable]
public sealed class PlayerResponseOption
{
    public string optionId;
    public string placeholderText;
    public int affectionDelta;
    public string nextBlockId;

    public PlayerResponseOption(string optionId, string placeholderText, int affectionDelta, string nextBlockId = null)
    {
        this.optionId = optionId;
        this.placeholderText = placeholderText;
        this.affectionDelta = affectionDelta;
        this.nextBlockId = nextBlockId;
    }
}

[Serializable]
public sealed class NpcSpeakingBlock
{
    public string blockId;
    public string[] npcTextFragments;
    public bool allowsPlayerResponse;
    public List<PlayerResponseOption> responseOptions;
    public string nextBlockId;
    public int noResponseAffectionDelta;
    public string noResponseNextBlockId;

    public NpcSpeakingBlock(
        string blockId,
        string[] npcTextFragments,
        bool allowsPlayerResponse,
        List<PlayerResponseOption> responseOptions,
        string nextBlockId = null,
        int noResponseAffectionDelta = -12,
        string noResponseNextBlockId = null)
    {
        this.blockId = blockId;
        this.npcTextFragments = npcTextFragments;
        this.allowsPlayerResponse = allowsPlayerResponse;
        this.responseOptions = responseOptions;
        this.nextBlockId = nextBlockId;
        this.noResponseAffectionDelta = noResponseAffectionDelta;
        this.noResponseNextBlockId = noResponseNextBlockId;
    }
}

public readonly struct ResponseResult
{
    public readonly bool WasChosen;
    public readonly PlayerResponseOption Option;
    public readonly int AffectionDelta;
    public readonly string NextBlockId;

    public ResponseResult(bool wasChosen, PlayerResponseOption option, int affectionDelta, string nextBlockId)
    {
        WasChosen = wasChosen;
        Option = option;
        AffectionDelta = affectionDelta;
        NextBlockId = nextBlockId;
    }
}

public static class StorySessionState
{
    private static readonly Dictionary<string, string> ChoicesByBlock = new Dictionary<string, string>();
    private static readonly HashSet<string> SelectedOptionIds = new HashSet<string>();
    private static readonly HashSet<string> MissedBlocks = new HashSet<string>();

    public static IReadOnlyDictionary<string, string> Choices => ChoicesByBlock;
    public static IReadOnlyCollection<string> SelectedOptions => SelectedOptionIds;
    public static IReadOnlyCollection<string> MissedResponseBlocks => MissedBlocks;

    public static void ResetForNewRun()
    {
        ChoicesByBlock.Clear();
        SelectedOptionIds.Clear();
        MissedBlocks.Clear();
    }

    public static void RecordChoice(string blockId, string optionId)
    {
        if (!string.IsNullOrWhiteSpace(blockId) && !string.IsNullOrWhiteSpace(optionId))
        {
            ChoicesByBlock[blockId] = optionId;
            SelectedOptionIds.Add(optionId);
        }
    }

    public static void RecordNoResponse(string blockId)
    {
        if (string.IsNullOrWhiteSpace(blockId))
        {
            return;
        }

        ChoicesByBlock[blockId] = "NO_RESPONSE";
        MissedBlocks.Add(blockId);
    }

    public static bool HasSelectedOption(string optionId)
    {
        return !string.IsNullOrWhiteSpace(optionId) && SelectedOptionIds.Contains(optionId);
    }

    public static bool WasBlockMissed(string blockId)
    {
        return !string.IsNullOrWhiteSpace(blockId) && MissedBlocks.Contains(blockId);
    }

    public static string GetChoiceForBlock(string blockId)
    {
        return !string.IsNullOrWhiteSpace(blockId) && ChoicesByBlock.TryGetValue(blockId, out string choice)
            ? choice
            : string.Empty;
    }

    public static void DebugDump()
    {
        Debug.Log("Choices recorded: " + ChoicesByBlock.Count);
    }
}
