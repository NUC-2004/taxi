# Level 0 Script

Current in-game copy for `Level0`, organized by block and branch.

## Overview

- Scene role: first real ride, disguised as tutorial
- NPC: unnamed in script here, but this is the key Level 0 passenger
- Tone target: indirect, restrained, late-night urban intimacy
- Player choice timing: short-window response, so option text stays compact

---

## Block `L0_OPEN`

**NPC**

1. Are you still taking fares?
2. Good.
3. Give me a second before I tell you where.
4. When I stop, say something.
5. Keys, space, mouse. Whatever's faster.

**Next Block**

- `L0_FIRST_RESPONSE`

---

## Block `L0_FIRST_RESPONSE`

**NPC**

1. That's better.
2. I hate feeling like I'm talking to the seat in front of me.

**Player Options**

- `L0_OPEN_WARM`  
  Text: `I'm here.`  
  Affection: `+2`  
  Next: `L0_OPEN_WARM_REPLY`

- `L0_OPEN_WRY`  
  Text: `We'll see.`  
  Affection: `+1`  
  Next: `L0_OPEN_WRY_REPLY`

- `L0_OPEN_FLAT`  
  Text: `Destination?`  
  Affection: `0`  
  Next: `L0_OPEN_FLAT_REPLY`

- `L0_OPEN_GUARD`  
  Text: `You're odd.`  
  Affection: `-1`  
  Next: `L0_OPEN_GUARD_REPLY`

**No Response**

- Affection: `-1`
- Next: `L0_OPEN_MISS`

---

## Block `L0_OPEN_WARM_REPLY`

**NPC**

1. Mm.
2. You say that like it's simple.

**Next Block**

- `L0_DEST_SETUP`

---

## Block `L0_OPEN_WRY_REPLY`

**NPC**

1. Fair enough.
2. I've probably earned that.

**Next Block**

- `L0_DEST_SETUP`

---

## Block `L0_OPEN_FLAT_REPLY`

**NPC**

1. Right.
2. Keep it practical.

**Next Block**

- `L0_DEST_SETUP`

---

## Block `L0_OPEN_GUARD_REPLY`

**NPC**

1. A little, yeah.
2. Long night.

**Next Block**

- `L0_DEST_SETUP`

---

## Block `L0_OPEN_MISS`

**NPC**

1. Okay.
2. Quiet works too.

**Next Block**

- `L0_DEST_SETUP`

---

## Block `L0_DEST_SETUP`

**NPC**

1. I said Crescent Hotel.
2. Then I kept walking.
3. Now I'm doing the same thing in your cab.

**Next Block**

- `L0_DEST_RESPONSE`

---

## Block `L0_DEST_RESPONSE`

**NPC**

1. Well?

**Player Options**

- `L0_DEST_ASK`  
  Text: `Then where?`  
  Affection: `+1`  
  Next: `L0_DEST_ASK_REPLY`

- `L0_DEST_SOFT`  
  Text: `Take your time.`  
  Affection: `+2`  
  Next: `L0_DEST_SOFT_REPLY`

- `L0_DEST_SHARP`  
  Text: `You're stalling.`  
  Affection: `-1`  
  Next: `L0_DEST_SHARP_REPLY`

- `L0_DEST_CURIOUS`  
  Text: `Who was there?`  
  Affection: `+1`  
  Next: `L0_DEST_CURIOUS_REPLY`

**No Response**

- Affection: `-1`
- Next: `L0_DEST_MISS`

---

## Block `L0_DEST_ASK_REPLY`

**NPC**

1. If I knew, I'd have said it already.
2. That's the whole problem.

**Next Block**

- `L0_NIGHT_CONTEXT`

---

## Block `L0_DEST_SOFT_REPLY`

**NPC**

1. That's nice of you.
2. Also kind of dangerous.

**Next Block**

- `L0_NIGHT_CONTEXT`

---

## Block `L0_DEST_SHARP_REPLY`

**NPC**

1. Maybe I am.
2. Still not the worst idea I've had tonight.

**Next Block**

- `L0_NIGHT_CONTEXT`

---

## Block `L0_DEST_CURIOUS_REPLY`

**NPC**

1. Too many people.
2. Too much polished glass.

**Next Block**

- `L0_NIGHT_CONTEXT`

---

## Block `L0_DEST_MISS`

**NPC**

1. You leave people room.
2. I can't tell if that's kind or lazy.

**Next Block**

- `L0_NIGHT_CONTEXT`

---

## Block `L0_NIGHT_CONTEXT`

**NPC**

1. It was one of those rooms where everybody looks expensive and nobody says what they mean.
2. Soft voices. Bright glass. Too much perfume.
3. You know the kind.

**Next Block**

- `L0_NIGHT_RESPONSE`

---

## Block `L0_NIGHT_RESPONSE`

**NPC**

1. What would you have said?

**Player Options**

- `L0_NIGHT_LEAVE`  
  Text: `Leave early.`  
  Affection: `+1`  
  Next: `L0_NIGHT_LEAVE_REPLY`

- `L0_NIGHT_ENDURE`  
  Text: `Smile, then go.`  
  Affection: `0`  
  Next: `L0_NIGHT_ENDURE_REPLY`

- `L0_NIGHT_REFUSE`  
  Text: `Say no.`  
  Affection: `+2`  
  Next: `L0_NIGHT_REFUSE_REPLY`

- `L0_NIGHT_DEFLECT`  
  Text: `Depends on them.`  
  Affection: `+1`  
  Next: `L0_NIGHT_DEFLECT_REPLY`

**No Response**

- Affection: `-1`
- Next: `L0_NIGHT_MISS`

---

## Block `L0_NIGHT_LEAVE_REPLY`

**NPC**

1. Yeah.
2. I almost did. A few times.

**Next Block**

- `L0_ROUTE_SETUP`

---

## Block `L0_NIGHT_ENDURE_REPLY`

**NPC**

1. That sounds like experience.
2. Not in a flattering way.

**Next Block**

- `L0_ROUTE_SETUP`

---

## Block `L0_NIGHT_REFUSE_REPLY`

**NPC**

1. Mm.
2. Easy word. Bad timing.

**Next Block**

- `L0_ROUTE_SETUP`

---

## Block `L0_NIGHT_DEFLECT_REPLY`

**NPC**

1. That's the annoying part.
2. They weren't being dramatic about it.

**Next Block**

- `L0_ROUTE_SETUP`

---

## Block `L0_NIGHT_MISS`

**NPC**

1. No answer.
2. Maybe that's an answer too.

**Next Block**

- `L0_ROUTE_SETUP`

---

## Block `L0_ROUTE_SETUP`

**NPC**

1. Take the river road.
2. Not the tunnel.
3. I don't want to feel boxed in yet.

**Next Block**

- `L0_ROUTE_RESPONSE`

---

## Block `L0_ROUTE_RESPONSE`

**NPC**

1. Would you?

**Player Options**

- `L0_ROUTE_BRIGHT`  
  Text: `Too bright.`  
  Affection: `0`  
  Next: `L0_ROUTE_BRIGHT_REPLY`

- `L0_ROUTE_RIVER`  
  Text: `River road.`  
  Affection: `+2`  
  Next: `L0_ROUTE_RIVER_REPLY`

- `L0_ROUTE_TUNNEL`  
  Text: `Take the tunnel.`  
  Affection: `-1`  
  Next: `L0_ROUTE_TUNNEL_REPLY`

- `L0_ROUTE_ASKWHY`  
  Text: `Why not?`  
  Affection: `+1`  
  Next: `L0_ROUTE_ASKWHY_REPLY`

**No Response**

- Affection: `-1`
- Next: `L0_ROUTE_MISS`

---

## Block `L0_ROUTE_BRIGHT_REPLY`

**NPC**

1. Exactly.
2. Some roads feel like they're watching you.

**Next Block**

- `L0_NAME_SETUP`

---

## Block `L0_ROUTE_RIVER_REPLY`

**NPC**

1. Right.
2. A few extra minutes can feel very generous.

**Next Block**

- `L0_NAME_SETUP`

---

## Block `L0_ROUTE_TUNNEL_REPLY`

**NPC**

1. You sound like my mother.
2. Which isn't a compliment, sorry.

**Next Block**

- `L0_NAME_SETUP`

---

## Block `L0_ROUTE_ASKWHY_REPLY`

**NPC**

1. Because once you're in, that's it.
2. No windows. No turns. Just forward.

**Next Block**

- `L0_NAME_SETUP`

---

## Block `L0_ROUTE_MISS`

**NPC**

1. You let me choose.
2. Most people like to feel useful sooner than that.

**Next Block**

- `L0_NAME_SETUP`

---

## Block `L0_NAME_SETUP`

**NPC**

1. His name is Daichi.
2. That probably tells you enough.
3. Or maybe it tells you nothing.

**Next Block**

- `L0_NAME_RESPONSE`

---

## Block `L0_NAME_RESPONSE`

**NPC**

1. Choose one.

**Player Options**

- `L0_NAME_MARRY`  
  Text: `Marry him.`  
  Affection: `-1`  
  Next: `L0_NAME_MARRY_REPLY`

- `L0_NAME_APOLOGIZE`  
  Text: `Apologize.`  
  Affection: `0`  
  Next: `L0_NAME_APOLOGIZE_REPLY`

- `L0_NAME_THANK`  
  Text: `Thank him.`  
  Affection: `0`  
  Next: `L0_NAME_THANK_REPLY`

- `L0_NAME_NONE`  
  Text: `None fit.`  
  Affection: `+2`  
  Next: `L0_NAME_NONE_REPLY`

**No Response**

- Affection: `-1`
- Next: `L0_NAME_MISS`

---

## Block `L0_NAME_MARRY_REPLY`

**NPC**

1. That's one way to put it.
2. A very tidy way.

**Next Block**

- `L0_DECISION_SETUP`

---

## Block `L0_NAME_APOLOGIZE_REPLY`

**NPC**

1. There was a lot of that going around.
2. Nobody sounded sorry, though.

**Next Block**

- `L0_DECISION_SETUP`

---

## Block `L0_NAME_THANK_REPLY`

**NPC**

1. He's used to that.
2. Makes rooms like that run smoothly.

**Next Block**

- `L0_DECISION_SETUP`

---

## Block `L0_NAME_NONE_REPLY`

**NPC**

1. Yeah.
2. Better.

**Next Block**

- `L0_DECISION_SETUP`

---

## Block `L0_NAME_MISS`

**NPC**

1. You passed.
2. Probably smart.

**Next Block**

- `L0_DECISION_SETUP`

---

## Block `L0_DECISION_SETUP`

**NPC**

1. It's stupid, but all night I've been waiting for somebody to say one thing that sounds real.
2. Not smart.
3. Just real.

**Next Block**

- `L0_DECISION_RESPONSE`

---

## Block `L0_DECISION_RESPONSE`

**NPC**

1. So. Give me one.

**Player Options**

- `L0_DECISION_GO`  
  Text: `Choose yourself.`  
  Affection: `+2`  
  Next: `L0_DECISION_GO_REPLY`

- `L0_DECISION_BACK`  
  Text: `Go back.`  
  Affection: `0`  
  Next: `L0_DECISION_BACK_REPLY`

- `L0_DECISION_TELL`  
  Text: `Tell him plainly.`  
  Affection: `+1`  
  Next: `L0_DECISION_TELL_REPLY`

- `L0_DECISION_DRIVER`  
  Text: `Not my call.`  
  Affection: `-1`  
  Next: `L0_DECISION_DRIVER_REPLY`

**No Response**

- Affection: `-2`
- Next: `L0_DECISION_MISS`

---

## Block `L0_DECISION_GO_REPLY`

**NPC**

1. That's annoyingly clear.
2. I almost believe you.

**Next Block**

- `L0_FINAL_APPROACH`

---

## Block `L0_DECISION_BACK_REPLY`

**NPC**

1. That would be easier.
2. Which doesn't make it wrong.

**Next Block**

- `L0_FINAL_APPROACH`

---

## Block `L0_DECISION_TELL_REPLY`

**NPC**

1. Plainly.
2. You've said that kind of thing before, haven't you?

**Next Block**

- `L0_FINAL_APPROACH`

---

## Block `L0_DECISION_DRIVER_REPLY`

**NPC**

1. No.
2. But people do say strange honest things to drivers.

**Next Block**

- `L0_FINAL_APPROACH`

---

## Block `L0_DECISION_MISS`

**NPC**

1. Right.
2. Leave it hanging.

**Next Block**

- `L0_FINAL_APPROACH`

---

## Block `L0_FINAL_APPROACH`

**NPC**

1. All right.
2. Not Crescent Hotel.
3. Shin-Ori Station.
4. If I miss the last express, maybe that's that.
5. If I make it...

**Next Block**

- `L0_FINAL_RESPONSE`

---

## Block `L0_FINAL_RESPONSE`

**NPC**

1. Finish it.

**Player Options**

- `L0_FINAL_BEGIN`  
  Text: `Then begin.`  
  Affection: `+3`  
  Next: `L0_END_BEGIN`

- `L0_FINAL_BREATHE`  
  Text: `Then choose.`  
  Affection: `+2`  
  Next: `L0_END_BREATHE`

- `L0_FINAL_CALL`  
  Text: `Then call him.`  
  Affection: `+1`  
  Next: `L0_END_CALL`

- `L0_FINAL_COLD`  
  Text: `Don't drift.`  
  Affection: `-1`  
  Next: `L0_END_COLD`

**No Response**

- Affection: `-2`
- Next: `L0_END_MISS`

---

## Block `L0_END_BEGIN`

**NPC**

1. Begin.
2. You make it sound almost normal.
3. Say it again in five minutes.

**Next Block**

- `L0_END_COMMON`

---

## Block `L0_END_BREATHE`

**NPC**

1. Maybe that's enough for one night.
2. Not brave.
3. Just enough.

**Next Block**

- `L0_END_COMMON`

---

## Block `L0_END_CALL`

**NPC**

1. You really like things neat.
2. I don't hate that.
3. Still... maybe.

**Next Block**

- `L0_END_COMMON`

---

## Block `L0_END_COLD`

**NPC**

1. No.
2. You're right, though.
3. A station's just a station.
4. The hard part starts after.

**Next Block**

- `L0_END_COMMON`

---

## Block `L0_END_MISS`

**NPC**

1. One last chance.
2. All right.

**Next Block**

- `L0_END_COMMON`

---

## Block `L0_END_COMMON`

**NPC**

1. There.
2. That's the station sign.
3. Funny. I started this ride trying to sound in control.
4. Maybe I just wanted somebody to answer back.
5. Thanks. Or... whatever this was.
6. Just don't forget me before the light changes.

**Next Block**

- `END`

---

## Notes For Revision

- Keep response text short enough to scan in under four seconds.
- Preserve block IDs and option IDs if later levels depend on `StorySessionState`.
- Safe places to revise tone without breaking logic:
  - NPC line wording
  - Option display text
  - Reply block wording
- Riskier changes:
  - Renaming block IDs
  - Renaming option IDs
  - Changing branch destinations already planned for later callbacks
