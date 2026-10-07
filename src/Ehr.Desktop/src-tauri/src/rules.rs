//! The rules for a phrase, checked here for an immediate answer. The server checks the same ones and has the last
//! word (QuickTextInput in src/Ehr.Domain/QuickTexts): change both together.

use std::collections::BTreeMap;

/// Problems with a phrase, by field ("shortcut" or "body"). Empty when it's fine.
pub type Problems = BTreeMap<&'static str, &'static str>;

/// Lengths count UTF-16 units, as .NET does, so the two sides agree.
pub fn check(shortcut: &str, body: &str) -> Problems {
    let mut problems = Problems::new();

    let rest = shortcut.strip_prefix('.').unwrap_or_default();
    if shortcut.trim().is_empty() {
        problems.insert("shortcut", "Enter a shortcut.");
    } else if shortcut.encode_utf16().count() > 32 {
        problems.insert("shortcut", "Use 32 characters or fewer.");
    } else if rest.is_empty()
        || !rest
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || c == '_' || c == '-')
    {
        problems.insert(
            "shortcut",
            "Start with a period, then letters, numbers, - or _.",
        );
    }

    if body.trim().is_empty() {
        problems.insert("body", "Enter the text.");
    } else if body.encode_utf16().count() > 4000 {
        problems.insert("body", "Use 4,000 characters or fewer.");
    }

    problems
}

/// Shortcuts are stored in lowercase, so .NAD and .nad are the same shortcut.
pub fn normalize(shortcut: &str) -> String {
    shortcut.to_lowercase()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_good_phrase_has_no_problems() {
        assert!(check(".nad", "No acute distress.").is_empty());
        assert!(check(".A_b-9", "Text").is_empty());
    }

    #[test]
    fn shortcuts_follow_the_server_rules() {
        for shortcut in [
            "",
            "nad",
            ".",
            ".two words",
            ".semi;colon",
            &format!(".{}", "a".repeat(32)),
        ] {
            assert!(
                check(shortcut, "Text").contains_key("shortcut"),
                "{shortcut:?} should be refused"
            );
        }
    }

    #[test]
    fn the_text_is_required_and_limited() {
        assert_eq!(check(".nad", "  ").get("body"), Some(&"Enter the text."));
        assert!(check(".nad", &"a".repeat(4001)).contains_key("body"));
        assert!(check(".nad", &"a".repeat(4000)).is_empty());
    }
}
