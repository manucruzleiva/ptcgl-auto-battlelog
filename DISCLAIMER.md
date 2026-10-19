# Disclaimer

PTCGL Auto Battle Log is an unofficial, free and open-source fan project. It is provided "as is",
without warranty of any kind (see LICENSE).

It works by loading a BepInEx plugin into the Pokémon TCG Live client. Modifying the game client
may violate the game's Terms of Service. Installing and using this software is entirely your own
decision and at your own risk.

The authors and contributors of this project are not responsible for any consequence of using it,
including but not limited to warnings, suspensions or bans of your game account, loss of data, or
any other damage.

What it does: it automates obtaining data that the game itself already offers you, the battle log
text produced by the Export button of the Battle Log screen, and saves it to a folder on your
computer. To name, index and seal each log, it also reads, during a match, match details that the
game client already holds: the match id, both players' display names and account ids, your deck
name, the seats and the winner. The opponent's name goes into the log's file name; the names, your
deck name and the match id into its index.csv row and its seal; account ids are stored only as
hashes, in the seal.

What it does not do: it does not take part in the battle. It does not show you any information,
does not automate any game action, and does not change cards, decks, matches or results. It sends
nothing to The Pokémon Company. Its only network traffic is the tray app checking this project's
GitHub releases for updates.

This project is not affiliated with, endorsed, sponsored or approved by Nintendo, The Pokémon
Company, The Pokémon Company International, Creatures Inc. or GAME FREAK inc. Pokémon and Pokémon
TCG Live are trademarks of their respective owners.
