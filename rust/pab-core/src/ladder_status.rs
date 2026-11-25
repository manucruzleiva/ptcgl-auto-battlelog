//! What the status window says about the tcgkb ranking (LadderStatus.cs): the three steps to a row
//! and why a player is not ranked yet. The rules are tcgkb's (docs/TCGKB-API.md, "PTCGL accounts").

use crate::tcgkb::Accounts;

pub const DISPUTED: &str = "disputed";

#[derive(Debug, Clone, Default, PartialEq)]
pub struct LadderStatus {
    pub has_account: bool,
    pub ranked: bool,
    /// A short line for the home page, e.g. "Ladder: 3 of 5 matches".
    pub title: String,
    /// Right of the title: "2 more to go", "#4 of 37".
    pub count: String,
    /// The third step's line and its explanation.
    pub step: String,
    pub why: String,
    /// The progress bar, 0 to 1.
    pub progress: f64,
}

impl LadderStatus {
    /// None until tcgkb answered with the ladder.
    pub fn from(me: Option<&Accounts>) -> Option<LadderStatus> {
        let me = me?;
        let ladder = me.ladder.as_ref()?;
        let (min, games) = (ladder.min_games.max(1), ladder.games.max(0));
        let has_account = me.accounts.iter().any(|a| a.status.as_deref() != Some(DISPUTED));
        if let Some(rank) = ladder.rank {
            return Some(LadderStatus {
                has_account,
                ranked: true,
                title: "On the ladder".into(),
                count: format!("#{rank} of {}", ladder.players),
                step: format!("{games} matches played"),
                why: "You're ranked by Elo, over every match of all your accounts.".into(),
                progress: 1.0,
            });
        }
        let left = (min - games).max(1);
        let why = if !has_account && !me.accounts.is_empty() {
            "Another tcgkb user claims your PTCGL account too, so its matches count for no one until one of you has a verified match.".to_string()
        } else if !has_account {
            format!("Play a match with the app running: its upload adds your PTCGL account. You need {min} matches before the ladder ranks you.")
        } else {
            format!("You have {games} of {min}. Until then you show up at the bottom of the ladder as \"Not ranked yet\", so a few lucky wins can't top the board.")
        };
        Some(LadderStatus {
            has_account,
            ranked: false,
            title: format!("Ladder: {games} of {min} matches"),
            count: format!("{left} more to go"),
            step: format!("Play {min} matches ({games} of {min})"),
            why,
            progress: (games as f64 / min as f64).min(1.0),
        })
    }
}
