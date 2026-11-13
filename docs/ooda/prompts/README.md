# OODA decision prompts

Use [shared.md](shared.md) followed by the [general game guide](game-guide.md) and one role prompt. [Operational](operational.md), [routine tactical](routine-tactical.md), and [surface defense](surface-defense.md) run through Codex Sol. Surface defense uses the shared instructions and its role prompt so it can respond quickly to attacks. [Urgent tactical](urgent-tactical.md) uses the shared and urgent instructions with a compact snapshot. Send the snapshot as a separate data message when the provider supports it. Enforce the response schema and native order checks in the host. Only include supported tools, tasks, destinations, and actions in a request.

OODA embeds these prompts in the mod. On the game thread, `DecisionPrompt.ForMission` takes a scoped player-view snapshot and adds the active localized briefing for Sol. Codex receives shared instructions in a stable developer message, followed by the role prompt, briefing, and compact snapshot on stdin. The briefing reader uses the game's loaded briefing pane, with the current mission's displayed description as a fallback. Urgent prompts use a compact snapshot with current options and a deadline.

Use the [problem solving guide](../decision-guide.md) to improve the shared instructions and tests. The runtime receives the actual briefing, visible updates, and allowed observations for that run. Expose the model's short decision reason and its input evidence in the decision window.

Operational and routine tactical decisions also receive [order planning and tool guidance](orders.md) for deadlines, holds, positioning, and native order progress.

Use these checks when tuning a prompt. Compare the chosen action and the evidence it cites. Wording can vary between runs.

| Input change | Expected behavior |
| --- | --- |
| A patrol brief gives two ships one arrival deadline. One ship is slower or its route is blocked. | Keep both ships in the objective. Take the slower ship's arrival estimate into account and request a replan for a blocked route. |
| A briefing warns of fast attack craft. A nearby track stays unknown after a challenge. | Keep it unknown. Track, investigate, or stay on task as permitted. The briefing and the unanswered challenge do not authorize a shot. |
| Rename every contact ID and permute the list order, leaving the observations the same. | Give the same treatment to the same evidence. Any referenced IDs must come from the new input. |
| Replace a current, authorized target with a bearing-only or lost track. | Drop any proposed shot that requires a current target. Choose to hold, observe, or reacquire as supported. |
| An escort's scout is pulled away or the threatened merchant changes course. | Reassess coverage of the merchant. Avoid chasing a contact at the cost of the escort objective. |
| A carrier batch is ready in an estimated five minutes while a patrol must be relieved in two. | Treat the timing as a coverage gap. Look for an available alternative or flag the gap. Keep preparing and airborne states distinct. |
| A heavily damaged ship can still fire, but the target already has an adequate salvo inbound. | Favor holding unless the snapshot supplies a concrete reason more fire advances the mission. |
| Remove the valid target or make the firing deadline expire before applying the result. | The host must leave the shot unavailable or reject the stale response even if the model asks to fire. |
