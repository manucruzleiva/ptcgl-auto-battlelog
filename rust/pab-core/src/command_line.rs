//! The tray's command line (CommandLine.cs). Flags ignore case; anything unknown is kept aside for
//! the log instead of failing: an old Run key or a shortcut edited by hand must still start the app.

pub const BACKGROUND: &str = "--background";
pub const FIRST_RUN: &str = "--first-run";
pub const UPDATED_FROM: &str = "--updated-from";
pub const PORTABLE: &str = "--portable";
pub const UNINSTALL: &str = "--uninstall";
pub const QUIET: &str = "--quiet";

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct CommandLine {
    pub background: bool,
    pub first_run: bool,
    pub portable: bool,
    pub uninstall: bool,
    pub quiet: bool,
    /// The version that was replaced; "" when the flag came without one; None without the flag.
    pub updated_from: Option<String>,
    pub unknown: Vec<String>,
}

impl CommandLine {
    pub fn parse<S: AsRef<str>>(args: impl IntoIterator<Item = S>) -> CommandLine {
        let list: Vec<String> = args.into_iter().map(|a| a.as_ref().trim().to_string()).collect();
        let mut cl = CommandLine::default();
        let mut i = 0;
        while i < list.len() {
            let arg = &list[i];
            let lower = arg.to_lowercase();
            match lower.as_str() {
                "" => {}
                BACKGROUND => cl.background = true,
                FIRST_RUN => cl.first_run = true,
                PORTABLE => cl.portable = true,
                UNINSTALL => cl.uninstall = true,
                QUIET => cl.quiet = true,
                UPDATED_FROM => {
                    if list.get(i + 1).is_some_and(|n| !n.starts_with("--")) {
                        i += 1;
                        cl.updated_from = Some(list[i].clone());
                    } else {
                        cl.updated_from = Some(String::new());
                    }
                }
                _ if lower.starts_with("--updated-from=") => cl.updated_from = Some(arg[UPDATED_FROM.len() + 1..].trim().to_string()),
                _ => cl.unknown.push(arg.clone()),
            }
            i += 1;
        }
        cl
    }

    pub fn updated(&self) -> bool {
        self.updated_from.is_some()
    }
}

/// One argument quoted for CreateProcess / CommandLineToArgvW: quotes only when needed, backslashes
/// doubled only where they precede a quote.
pub fn quote(arg: &str) -> String {
    if !arg.is_empty() && !arg.contains([' ', '\t', '\n', '\u{b}', '"']) {
        return arg.to_string();
    }
    let mut sb = String::from("\"");
    let mut backslashes = 0;
    for c in arg.chars() {
        if c == '\\' {
            backslashes += 1;
            continue;
        }
        if c == '"' {
            sb.push_str(&"\\".repeat(backslashes * 2 + 1));
        } else {
            sb.push_str(&"\\".repeat(backslashes));
        }
        sb.push(c);
        backslashes = 0;
    }
    sb.push_str(&"\\".repeat(backslashes * 2));
    sb.push('"');
    sb
}

pub fn join<S: AsRef<str>>(args: impl IntoIterator<Item = S>) -> String {
    args.into_iter().map(|a| quote(a.as_ref())).collect::<Vec<_>>().join(" ")
}

/// The Run key value: "<exe>" --background, plus --portable for a portable copy without its marker.
pub fn run_command(exe: &str, append_portable: bool) -> String {
    let args: &[&str] = if append_portable { &[BACKGROUND, PORTABLE] } else { &[BACKGROUND] };
    format!("\"{exe}\" {}", join(args))
}
