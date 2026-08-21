using Xunit;

namespace SarnautCore.Network.Tests;

public sealed class ZoneNetworkLoopHudCommandSourceTests
{
    [Fact]
    public void RequestsUseOneMonotonicPerSessionSequenceAndRejectForeignRevisions()
    {
        string source = ReadZoneNetworkLoop();

        Assert.Contains("private ulong _nextHudRequestId = 1;", source, StringComparison.Ordinal);
        Assert.Contains("_nextHudRequestId = unchecked(requestId + 1);", source, StringComparison.Ordinal);
        Assert.Contains(
            "command.ExpectedRevision.SourceEpoch == _hudSession.SourceEpoch",
            source,
            StringComparison.Ordinal);
        Assert.Contains("revision != 0", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionAndTargetRequestsUseTheAuthoredSlotAndServerTargetAuthority()
    {
        string source = ReadZoneNetworkLoop();
        string action = Case(source, "ActivateAction", "MoveInventoryItem");
        string target = Case(source, "SelectWorldEntity", "InteractWorldEntity");

        Assert.Contains("TargetSelect = new TargetSelect", target, StringComparison.Ordinal);
        Assert.Contains("TargetEntityId = command.EntityId", target, StringComparison.Ordinal);
        Assert.Contains("RequestId = targetRequestId", target, StringComparison.Ordinal);

        Assert.Contains("SlotIndex = actionSlot", action, StringComparison.Ordinal);
        Assert.Contains("ClientTick = _timeline.LatestServerTick", action, StringComparison.Ordinal);
        Assert.Contains("ExpectedRevision = actionRevision", action, StringComparison.Ordinal);
        Assert.DoesNotContain("command.Value", action, StringComparison.Ordinal);
        Assert.DoesNotContain("AbilityUse", action, StringComparison.Ordinal);
    }

    [Fact]
    public void InventoryAndLootRequestsPreserveOnlyWireRepresentableOperations()
    {
        string source = ReadZoneNetworkLoop();

        Assert.Contains("InventoryMove = new InventoryMove", source, StringComparison.Ordinal);
        Assert.Contains("FromSlot = fromSlot", source, StringComparison.Ordinal);
        Assert.Contains("ToSlot = toSlot", source, StringComparison.Ordinal);
        Assert.Contains("if (command.Flag)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("case HudCommandKind.DropInventoryItem:", source, StringComparison.Ordinal);
        Assert.DoesNotContain("case HudCommandKind.UseInventoryItem:", source, StringComparison.Ordinal);
        Assert.DoesNotContain("case HudCommandKind.DressInventoryItem:", source, StringComparison.Ordinal);
        Assert.DoesNotContain("case HudCommandKind.UndressInventoryItem:", source, StringComparison.Ordinal);

        Assert.Contains("LootTakeItem = new LootTakeItem", source, StringComparison.Ordinal);
        Assert.Contains("LootTakeMoney = new LootTakeMoney", source, StringComparison.Ordinal);
        Assert.Contains("LootTakeAll = new LootTakeAll", source, StringComparison.Ordinal);
        Assert.Contains("LootClose = new LootClose { RequestId = lootCloseRequestId }", source, StringComparison.Ordinal);
        Assert.Contains("if (command.Amount != -1)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void QuestRequestsKeepRevisionNpcRewardAndShareResponseSemantics()
    {
        string source = ReadZoneNetworkLoop();

        Assert.Contains("QuestAbandon = new QuestAbandon", source, StringComparison.Ordinal);
        Assert.Contains("QuestShare = new QuestShare", source, StringComparison.Ordinal);
        Assert.Contains("QuestShareResponse = new QuestShareResponse", source, StringComparison.Ordinal);
        Assert.Contains("Accept = command.Kind == HudCommandKind.AcceptSharedQuest", source, StringComparison.Ordinal);
        Assert.Contains("QuestAccept = new QuestAccept", source, StringComparison.Ordinal);
        Assert.Contains("StarterEntityId = starterEntityId", source, StringComparison.Ordinal);
        Assert.Contains("QuestTurnIn = new QuestTurnIn", source, StringComparison.Ordinal);
        Assert.Contains("FinisherEntityId = finisherEntityId", source, StringComparison.Ordinal);
        Assert.Contains("RewardIndex = rewardIndex", source, StringComparison.Ordinal);
    }

    private static string Case(string source, string kind, string nextKind)
    {
        int start = source.IndexOf($"case HudCommandKind.{kind}:", StringComparison.Ordinal);
        int end = source.IndexOf($"case HudCommandKind.{nextKind}:", start, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing {kind} HUD command case.");
        Assert.True(end > start, $"Missing command case after {kind}.");
        return source[start..end];
    }

    private static string ReadZoneNetworkLoop()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "src", "zone", "ZoneNetworkLoop.cs");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException("Could not locate src/zone/ZoneNetworkLoop.cs from the test output directory.");
    }
}
