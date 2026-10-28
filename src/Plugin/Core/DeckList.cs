using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PtcglAutoBattleLog.Core
{
    /// <summary>
    /// The player's own deck in one match, as the client holds it (playerDetails[me].deckInfo):
    /// PTCGL card ids (sv8_174) and their counts. The opponent's deck is never known to the client.
    /// Saved once per distinct deck as decks\&lt;sha256&gt;.deck in the logs folder; the seal (v2) of each
    /// log names it by that SHA-256.
    /// The format is the contract in docs/LOG-SEAL.md ("Deck file").
    /// </summary>
    public sealed class DeckList
    {
        public const string Extension = ".deck";
        public const int Version = 1;
        /// <summary>A PTCGL deck has 60 cards (40 in Build &amp; Battle); far more ids than that is not a deck.</summary>
        public const int MaxCards = 200;
        private const int MaxIdLength = 64;

        public string DeckGuid;
        public long? IterationCount;
        /// <summary>The client's deckName as given (may be markup such as "&lt;i&gt;&lt;/i&gt;"); null when empty.</summary>
        public string Name;
        public string DeckDefinitionId;
        /// <summary>The client's size, normally the sum of the counts.</summary>
        public int Size;
        /// <summary>Card id → count (&gt; 0), in ordinal order of the ids.</summary>
        public readonly SortedDictionary<string, int> Cards = new SortedDictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// The deck held by a deckInfo object, read by reflection (cards, size, deckDefinitionID,
        /// DeckGuid, IterationCount, deckName). Null when it has no cards. Never throws.
        /// </summary>
        public static DeckList FromDeckInfo(object deckInfo)
        {
            if (deckInfo == null) return null;
            try
            {
                var cards = ReflectionGameApi.Get(deckInfo, "cards") as IEnumerable;
                if (cards == null) return null;
                var deck = new DeckList();
                int seen = 0;
                foreach (var entry in cards)
                {
                    if (entry == null) continue;
                    if (++seen > MaxCards) return null;
                    var id = ReflectionGameApi.Get(entry, "Key") as string;
                    var count = AsLong(ReflectionGameApi.Get(entry, "Value"));
                    if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength || count == null || count <= 0 || count > 99) continue;
                    deck.Cards[id] = (int)count.Value;
                }
                if (deck.Cards.Count == 0) return null;
                var size = AsLong(ReflectionGameApi.Get(deckInfo, "size"));
                deck.Size = size.HasValue && size.Value > 0 && size.Value <= 9999 ? (int)size.Value : deck.Sum();
                deck.DeckGuid = NonEmpty(AsString(ReflectionGameApi.Get(deckInfo, "DeckGuid")));
                deck.IterationCount = AsLong(ReflectionGameApi.Get(deckInfo, "IterationCount"));
                deck.Name = NonEmpty(AsString(ReflectionGameApi.Get(deckInfo, "deckName")));
                deck.DeckDefinitionId = NonEmpty(AsString(ReflectionGameApi.Get(deckInfo, "deckDefinitionID")));
                return deck;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public int Sum()
        {
            int n = 0;
            foreach (var c in Cards.Values) n += c;
            return n;
        }

        /// <summary>
        /// The file's one line: keys in this order, cards in ordinal order of their ids, no spaces,
        /// pure ASCII (escaped as in the seal), no line break at the end.
        /// </summary>
        public string ToJson()
        {
            var sb = new StringBuilder(64 + Cards.Count * 16);
            sb.Append("{\"v\":").Append(Version.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"deckGuid\":").Append(LogSealer.Quote(DeckGuid));
            sb.Append(",\"iterationCount\":").Append(IterationCount.HasValue ? IterationCount.Value.ToString(CultureInfo.InvariantCulture) : "null");
            sb.Append(",\"name\":").Append(LogSealer.Quote(Name));
            sb.Append(",\"deckDefinitionId\":").Append(LogSealer.Quote(DeckDefinitionId));
            sb.Append(",\"size\":").Append(Size.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"cards\":{");
            bool first = true;
            foreach (var c in Cards)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(LogSealer.Quote(c.Key)).Append(':').Append(c.Value.ToString(CultureInfo.InvariantCulture));
            }
            return sb.Append("}}").ToString();
        }

        /// <summary>The bytes of the .deck file.</summary>
        public byte[] ToBytes()
        {
            return Encoding.ASCII.GetBytes(ToJson());
        }

        /// <summary>Same file content.</summary>
        public static bool Same(DeckList a, DeckList b)
        {
            if (a == null || b == null) return a == b;
            return a.ToJson() == b.ToJson();
        }

        private static string AsString(object o) { return o == null ? null : o.ToString(); }
        private static string NonEmpty(string s) { return string.IsNullOrEmpty(s) ? null : s; }

        private static long? AsLong(object o)
        {
            if (o == null || o is bool) return null;
            try { return Convert.ToInt64(o, CultureInfo.InvariantCulture); } catch { return null; }
        }
    }
}
