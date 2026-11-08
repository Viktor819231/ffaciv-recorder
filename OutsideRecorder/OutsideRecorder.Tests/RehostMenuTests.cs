namespace OutsideRecorderTests;

public sealed class RehostMenuTests
{
    [Fact]
    public void RepeatedNewMapsReplaceThePreviousGameWithTheNewLobby()
    {
        var state = new RecorderStatus { Game="Civ V", Layout="BNW" };
        for (var map=0; map<8; map++)
        {
            state = state with { Recording=true, Identity=$"{map:x8}-12345678", Context=new string((char)('a'+map),32),
                Folder="session-"+map, Turn=42, GameStarted=DateTimeOffset.Now, LastSaveAt=DateTimeOffset.Now,
                LastAutosave="previous-map.Civ5Save", Paused=true, SinglePlayerSkipped=true, Events=500 };
            var lobby = new LobbySnapshot { GameStarted=false, GameType=1, GameName="New map "+map,
                Slots=[new LobbySlot {Id=0,Status=3}] };
            Assert.True(lobby.IsStagingRoom);
            state = state.InLobby(lobby);
            Assert.Same(lobby,state.Lobby);
            Assert.False(state.Recording); Assert.False(state.Paused); Assert.False(state.SinglePlayerSkipped);
            Assert.Null(state.Identity); Assert.Null(state.Context); Assert.Null(state.Folder); Assert.Null(state.Turn);
            Assert.Null(state.GameStarted); Assert.Null(state.LastSaveAt); Assert.Null(state.LastAutosave);
            Assert.Empty(state.Players); Assert.Equal(0,state.Events);
            Assert.Equal("Civ V",state.Game); Assert.Equal("BNW",state.Layout);
        }
    }

    [Fact]
    public void ClosingCivAfterTheGameReturnedToTheLobbyLeavesNoLobbyCard()
    {
        // 2026-11-08: the game ended in the staging room, Civ closed a second later, the "Multiplayer lobby" card stayed 14 hours
        var lobby = new LobbySnapshot { GameStarted=false, GameType=1, GameName="ffa dlc", Slots=[new LobbySlot {Id=0,Status=3}] };
        var state = new RecorderStatus { Game="Civ V", Layout="BNW", CivDetected=true, MenuObservedAt=1 }.InLobby(lobby);
        state = state.CivGone();
        Assert.Null(state.Lobby); Assert.False(state.CivDetected); Assert.Null(state.MenuObservedAt);
        Assert.Null(state.Game); Assert.Null(state.Layout); Assert.False(state.Recording);
        Assert.Equal("Waiting for Civilization V to start",state.State);
    }
}
