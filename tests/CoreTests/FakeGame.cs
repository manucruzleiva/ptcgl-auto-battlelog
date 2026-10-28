using System;
using System.Collections.Generic;
using System.Linq;

// Shapes copied from the real client: global-namespace MatchManager behind a generic singleton,
// static NetworkMatchController, and the exporter with a static OnExport event.

public class MonoSingleton<T> where T : class
{
    public static T instance { get; set; }
}

public class MatchManager : MonoSingleton<MatchManager>
{
    public List<_Rainier.Scripts.BattleLog.IBattleLogData> Entries = new List<_Rainier.Scripts.BattleLog.IBattleLogData>();
    public IEnumerable<_Rainier.Scripts.BattleLog.IBattleLogData> BattleLogExportData => Entries.Count == 0 ? null : Entries;
}

public class MatchBoard { public string matchID; }

public class MatchInfo
{
    public bool gameOver;
    public int winner;
    public bool isPlayer1;
    public string accountID = "me-123";
    public string matchID;                      // fallback when the board has none
    public MatchBoard board = new MatchBoard();
    public QueueKind queueType = QueueKind.Ranked;   // made up, for the research dump test (issue #14)
    public MatchBoard GetBoardState() => board;
}

public enum QueueKind { Casual, Ranked, BuildAndBattle }

public class DeckInfo
{
    public int size;
    public string DeckGuid;
    public int IterationCount;
    public Dictionary<string, int> cards = new Dictionary<string, int>();
    public string deckName;
    public string deckDefinitionID;
}
public class PlayerDetail { public string playerId; public string playerName; public DeckInfo deckInfo; }

public enum GameplayType { Ranked, Casual, BuildAndBattle }
public class SignedAccountId { public string id; }

public static class NetworkMatchController
{
    public static MatchInfo currentMatchInfo { get; set; }
    public static List<PlayerDetail> playerDetails;
    // The queue and the format (issue #14). Nullable here only so a test can take the value away.
    public static GameplayType? gameplayType = GameplayType.Ranked;
    public static MatchLogic.GameMode gameMode = MatchLogic.GameMode.Standard;
    public static SignedAccountId directMatchOpponent;
    public static string lobbyCode = "";
}

namespace MatchLogic
{
    public enum GameMode { NONE, Base, Standard, Speed, Debug, Expanded, TrainerTrials, BuildAndBattle }
}

namespace _Rainier.Scripts.BattleLog
{
    public interface IBattleLogData { string Line { get; } }
    public class Entry : IBattleLogData { public string Line { get; set; } }

    public class BattleLogExporter
    {
        public static event Action<string> OnExport;
        public static string Clipboard = "player clipboard";
        public static int Exports;

        public void ExportBattleLog(IEnumerable<IBattleLogData> data)
        {
            Exports++;
            var text = string.Join("\n", data.Select(d => d.Line));
            Clipboard = text;
            OnExport?.Invoke(text);
        }
    }
}
