# Gameplay HUD addon surface

This document fixes the addon-facing names for the M2 gameplay HUD under ADR 0019. The Lua host is not part of M2, so the names below are reserved contracts. The current C# event is the source that a later Lua adapter will publish.

Addon payloads use canonical content IDs and localization keys. They never expose Godot nodes, converted resource paths, or translated display strings. An addon can translate a key or show the canonical slug.

## Input and focus

`GameplayFocusOwner` is the only module allowed to decide pointer capture or window focus.

| Addon event | C# source | Payload |
|---|---|---|
| `HUD_FOCUS_CHANGED` | `GameplayFocusOwner.Changed` | `focused_window`, `mouse_captured`, `world_input_enabled` |

Public commands are `Open(GameplayWindow)`, `Close(GameplayWindow)`, `Cancel()`, and `TryCaptureWorld()`. Escape closes the top window, then releases a captured mouse, then leaves walkabout on a later press. Left-click never captures the mouse.

## Target frame

View model: `TargetViewModel`.

| Addon event | C# source | Payload |
|---|---|---|
| `TARGET_CHANGED` | `TargetViewModel.Changed` | `entity_id`, `name_key`, `content_id`, `level`, `health`, `max_health`, `alive` |
| `TARGET_DIED` | `TargetViewModel.TargetDied` | `entity_id` |

Public commands are `Select(EntityHudSnapshot)`, `Clear()`, and the two authoritative `Apply` overloads for combat and death events.

## Ability bar

View model: `AbilityBarViewModel`. M2 has one global cooldown shared by every slot.

| Addon event | C# source | Payload |
|---|---|---|
| `ABILITY_BAR_CHANGED` | `AbilityBarViewModel.Changed` | `abilities`, `cooldown_remaining_seconds`, `cooldown_fraction` |
| `ABILITY_USE_REQUESTED` | `AbilityBarViewModel.AbilityRequested` | `ability_id`, `target_entity_id`, `client_tick` |

Public commands are `TryRequestUse(slot, target, tick)`, `Apply(CombatEvent, ownEntityId)`, and `Advance(deltaSeconds)`. Rejected casts do not start the cooldown.

## Floating damage and death feedback

View models: `DamageNumberPoolViewModel` and `DeathFeedbackViewModel`.

| Addon event | C# source | Payload |
|---|---|---|
| `DAMAGE_NUMBER_SPAWNED` | `DamageNumberPoolViewModel.Spawned` | `pool_index`, `entity_id`, `amount`, `critical`, `remaining_seconds` |
| `DAMAGE_NUMBER_EXPIRED` | `DamageNumberPoolViewModel.Expired` | `pool_index`, `entity_id` |
| `DEATH_FEEDBACK_CHANGED` | `DeathFeedbackViewModel.Changed` | `kind`, `entity_id`, `remaining_seconds` |

The damage pool has a fixed capacity. A full pool recycles its oldest active entry. Death feedback reports `TargetDefeated`, `PlayerDied`, or `Respawned` and expires on its timer.

## Loot

View model: `LootWindowViewModel`.

| Addon event | C# source | Payload |
|---|---|---|
| `LOOT_CHANGED` | `LootWindowViewModel.Changed` | `corpse_entity_id`, `money`, `items`, `last_refusal`, `is_open` |
| `LOOT_TAKE_REQUESTED` | `LootWindowViewModel.TakeRequested` | `corpse_entity_id` |
| `LOOT_CLOSED` | `LootWindowViewModel.Closed` | none |

Public commands are `Apply(LootOffer)`, `Apply(LootResult)`, `RequestTake()`, and `Close()`. M2 takes the whole offer. A refusal preserves it; a successful empty result closes the window.

## Inventory and bags

View model: `InventoryViewModel`.

| Addon event | C# source | Payload |
|---|---|---|
| `INVENTORY_CHANGED` | `InventoryViewModel.Changed` | `capacity`, `slots`, `currency` |

Public commands are `Apply(InventoryUpdate)`, `TryAdd(itemId, count, out rejection)`, and `TryMove(fromSlot, toSlot)`. `TryAdd` merges partial stacks before it counts free slots and rejects atomically when the full count cannot fit.

## Quest log and tracker

View models: `QuestLogViewModel` and `QuestTrackerViewModel`. Both consume the protocol-neutral `QuestUpdate` record.

| Addon event | C# source | Payload |
|---|---|---|
| `QUEST_LOG_CHANGED` | `QuestLogViewModel.Changed` | `quests`, `selected_quest` |
| `QUEST_ABANDON_REQUESTED` | `QuestLogViewModel.AbandonRequested` | `quest_id` |
| `QUEST_TRACKER_CHANGED` | `QuestTrackerViewModel.Changed` | `quests`, visible objectives and counters |
| `QUEST_COMPLETED` | `QuestTrackerViewModel.QuestCompleted` | `quest_id` |
| `QUEST_TURNED_IN` | `QuestTrackerViewModel.QuestTurnedIn` | `quest_id` |

The log exposes `Apply`, `Select`, and `RequestAbandonSelected`. The tracker exposes `Apply`. Internal objectives never appear in either widget.

## Quest dialogue

View model: `QuestDialogueViewModel`.

| Addon event | C# source | Payload |
|---|---|---|
| `QUEST_DIALOGUE_CHANGED` | `QuestDialogueViewModel.Changed` | `mode`, `quest`, `npc_entity_id`, `last_refusal`, `last_reward` |
| `QUEST_ACCEPT_REQUESTED` | `QuestDialogueViewModel.AcceptRequested` | `quest_id`, `npc_entity_id` |
| `QUEST_TURN_IN_REQUESTED` | `QuestDialogueViewModel.TurnInRequested` | `quest_id`, `npc_entity_id` |
| `QUEST_DIALOGUE_CLOSED` | `QuestDialogueViewModel.Closed` | none |

Public commands are `ShowOffer`, `ShowTurnIn`, `RequestAccept`, `RequestTurnIn`, `Apply`, and `Close`. Requests leave the panel open until an authoritative quest update confirms the state change.

## Envelope dispatch

`GameplayHudViewModel.Route(ServerMessage)` is the single server-event entry point. It dispatches spawn, despawn, combat, death, loot, inventory, and quest messages into the view models above. `QuestStateUpdateAdapter` maps the owning client's wire update to `QuestUpdate`, including objective indexes and counters, refusals, and turn-in rewards. The wire omits NPC entity ids, so `BeginInteraction(entityId)` binds an offer or completable response to the NPC the player used. No Godot type crosses this seam.
