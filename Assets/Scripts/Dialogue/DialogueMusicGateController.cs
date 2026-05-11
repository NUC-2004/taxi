using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public sealed class DialogueMusicGateController : MonoBehaviour
{
    private const int RequiredPlayerTrackSwitchCount = 2;
    private const string RockStagingTrackResourcePath = "Audio/Level0Bgm";
    private const string ClassicalStagingTrackResourcePath = "Audio/CityOfLove";
    private const string RockTrackResourcePath = "Audio/ThisHeavyMetal";
    private const string ClassicalTrackResourcePath = "Audio/WaltzAFlatMajorOp69No1";

    private static readonly Dictionary<string, string> RequiredTrackByBlockId = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        { "L3_B2_RADIO_ROCK_SETUP", RockTrackResourcePath },
        { "L3_B4_RADIO_CLASSICAL_SETUP", ClassicalTrackResourcePath }
    };

    private DialogueSequenceManager dialogueManager;
    private string gatedBlockId = string.Empty;
    private string requiredTrackResourcePath = string.Empty;
    private int playerTrackSwitchCount;
    private bool suppressAutomaticTrackChangeEvent;

    public bool IsAwaitingRequiredTrack =>
        !string.IsNullOrWhiteSpace(gatedBlockId) &&
        !string.IsNullOrWhiteSpace(requiredTrackResourcePath);

    private void Awake()
    {
        dialogueManager = GetComponent<DialogueSequenceManager>();
        Subscribe();
    }

    private void OnDisable()
    {
        Unsubscribe();
        ClearGate();
    }

    private void Subscribe()
    {
        if (dialogueManager == null)
        {
            return;
        }

        dialogueManager.BlockChanged -= HandleBlockChanged;
        dialogueManager.NpcFragmentPresented -= HandleNpcFragmentPresented;
        dialogueManager.PhaseChanged -= HandlePhaseChanged;
        dialogueManager.SequenceCompleted -= HandleSequenceEnded;
        dialogueManager.SequenceFailed -= HandleSequenceEnded;
        SceneMusicController.TrackChanged -= HandleTrackChanged;

        dialogueManager.BlockChanged += HandleBlockChanged;
        dialogueManager.NpcFragmentPresented += HandleNpcFragmentPresented;
        dialogueManager.PhaseChanged += HandlePhaseChanged;
        dialogueManager.SequenceCompleted += HandleSequenceEnded;
        dialogueManager.SequenceFailed += HandleSequenceEnded;
        SceneMusicController.TrackChanged += HandleTrackChanged;
    }

    private void Unsubscribe()
    {
        if (dialogueManager != null)
        {
            dialogueManager.BlockChanged -= HandleBlockChanged;
            dialogueManager.NpcFragmentPresented -= HandleNpcFragmentPresented;
            dialogueManager.PhaseChanged -= HandlePhaseChanged;
            dialogueManager.SequenceCompleted -= HandleSequenceEnded;
            dialogueManager.SequenceFailed -= HandleSequenceEnded;
        }

        SceneMusicController.TrackChanged -= HandleTrackChanged;
    }

    private void HandleBlockChanged(NpcSpeakingBlock block, int index, int total)
    {
        string blockId = block != null ? block.blockId : string.Empty;
        if (!string.Equals(blockId, gatedBlockId, StringComparison.Ordinal))
        {
            ClearGate();
        }
    }

    private void HandleNpcFragmentPresented(NpcSpeakingBlock block, int fragmentIndex, int fragmentCount)
    {
        if (dialogueManager == null || block == null || fragmentIndex != fragmentCount - 1)
        {
            return;
        }

        if (!RequiredTrackByBlockId.TryGetValue(block.blockId, out string trackResourcePath))
        {
            return;
        }

        gatedBlockId = block.blockId;
        requiredTrackResourcePath = trackResourcePath;
        playerTrackSwitchCount = 0;
        dialogueManager.SetNpcAdvanceLocked(true);
        PrepareGateTrack();
    }

    private void HandlePhaseChanged(DialoguePhase phase)
    {
        if (phase != DialoguePhase.NpcSpeaking)
        {
            ClearGate();
        }
    }

    private void HandleSequenceEnded()
    {
        ClearGate();
    }

    private void HandleTrackChanged(string trackResourcePath)
    {
        if (suppressAutomaticTrackChangeEvent)
        {
            suppressAutomaticTrackChangeEvent = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(gatedBlockId) ||
            string.IsNullOrWhiteSpace(requiredTrackResourcePath))
        {
            return;
        }

        playerTrackSwitchCount++;
        if (playerTrackSwitchCount < RequiredPlayerTrackSwitchCount ||
            !string.Equals(trackResourcePath, requiredTrackResourcePath, StringComparison.Ordinal))
        {
            return;
        }

        StartCoroutine(ContinueNextFrame());
    }

    private IEnumerator ContinueNextFrame()
    {
        yield return null;

        if (dialogueManager == null ||
            string.IsNullOrWhiteSpace(gatedBlockId) ||
            !string.Equals(dialogueManager.ActiveBlockId, gatedBlockId, StringComparison.Ordinal) ||
            playerTrackSwitchCount < RequiredPlayerTrackSwitchCount ||
            !SceneMusicController.IsCurrentTrack(requiredTrackResourcePath))
        {
            yield break;
        }

        dialogueManager.SetNpcAdvanceLocked(false);
        dialogueManager.ForceNpcAdvance();
        gatedBlockId = string.Empty;
        requiredTrackResourcePath = string.Empty;
        playerTrackSwitchCount = 0;
        suppressAutomaticTrackChangeEvent = false;
    }

    private void ClearGate()
    {
        gatedBlockId = string.Empty;
        requiredTrackResourcePath = string.Empty;
        playerTrackSwitchCount = 0;
        suppressAutomaticTrackChangeEvent = false;
        if (dialogueManager != null)
        {
            dialogueManager.SetNpcAdvanceLocked(false);
        }
    }

    private void PrepareGateTrack()
    {
        string stagingTrackResourcePath = GetStagingTrackForRequiredTrack(requiredTrackResourcePath);
        if (string.IsNullOrWhiteSpace(stagingTrackResourcePath) ||
            SceneMusicController.IsCurrentTrack(stagingTrackResourcePath))
        {
            return;
        }

        suppressAutomaticTrackChangeEvent = true;
        if (!SceneMusicController.PlayTrackByResourcePath(stagingTrackResourcePath))
        {
            suppressAutomaticTrackChangeEvent = false;
        }
    }

    private static string GetStagingTrackForRequiredTrack(string trackResourcePath)
    {
        if (string.Equals(trackResourcePath, RockTrackResourcePath, StringComparison.Ordinal))
        {
            return RockStagingTrackResourcePath;
        }

        if (string.Equals(trackResourcePath, ClassicalTrackResourcePath, StringComparison.Ordinal))
        {
            return ClassicalStagingTrackResourcePath;
        }

        return string.Empty;
    }
}
