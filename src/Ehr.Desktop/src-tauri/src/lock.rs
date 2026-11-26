//! When the desktop app's window locks (the expansion plan's step 19a, proposed FR-68), as a state machine that knows
//! nothing of Tauri: main.rs feeds it the page's activity, its recordings, what the server said and the time, and shows
//! what it says.
//!
//! - 15 minutes after the last key, click, tap, wheel turn or typing in the window, warned 2 minutes before, as a web
//!   sign-in idles out (SessionTimes.Idle and Warning in src/Ehr.Domain/Accounts/SessionTimes.cs: change them together).
//! - At the sign-in's 12-hour end (SessionTimes.Longest), warned once 2 minutes before, or as soon as the server says it
//!   has passed. Once only: a lock opened then with the password alone, because no server could complete a sign-in,
//!   isn't locked again by each server that says so; every lock after it asks for a full sign-in.
//! - When the server ends the session (a changed password, the account locked or disabled, the session's 30 days).
//! - When the sign-in's 30 days pass by this computer's clock, how long the offline copy may be opened without one,
//!   for a full sign-in with the password alone never enough, whether or not a server can be reached (the expansion
//!   plan's step 20, Tahseen's 14b: a revoked computer that never connects again locks its data, and deletes nothing
//!   until it connects). The session is kept, so the next server to answer can say what became of it.
//! - At launch, whenever a sign-in is kept, so quitting never skips the lock.
//!
//! A recording being made counts as activity and holds every lock and warning off, for 5 minutes at most: the desktop
//! records 60 seconds at most, and a page that never says it stopped mustn't keep the window open. Sync doesn't count.
//! Time is measured on the wall clock as well as the monotonic one, the larger counting: Rust's Instant doesn't advance
//! while a Mac sleeps, so time asleep counts through the wall clock, and setting the clock back gains nothing.

use std::time::{Duration, Instant, SystemTime};

use serde::Serialize;

pub const IDLE: Duration = Duration::from_secs(15 * 60);
pub const WARNING: Duration = Duration::from_secs(2 * 60);
pub const LONGEST: Duration = Duration::from_secs(12 * 60 * 60);
/// The most a recording holds the lock off.
pub const RECORDING_HOLD: Duration = Duration::from_secs(5 * 60);

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Times {
    pub idle: Duration,
    pub warning: Duration,
    pub longest: Duration,
}

impl Times {
    pub const STANDARD: Times = Times {
        idle: IDLE,
        warning: WARNING,
        longest: LONGEST,
    };

    /// Shorter times, for checking the lock by hand (main.rs reads them in development builds only). Never longer than
    /// the standard ones; the warning keeps its share of the idle time, 2 minutes of 15.
    pub fn shortened(idle: Option<Duration>, longest: Option<Duration>) -> Times {
        let shorter = |time: Option<Duration>, standard: Duration| {
            time.filter(|time| !time.is_zero() && *time < standard)
                .unwrap_or(standard)
        };
        let idle = shorter(idle, IDLE);
        Times {
            idle,
            warning: idle * 2 / 15,
            longest: shorter(longest, LONGEST),
        }
    }
}

/// A moment, on both clocks.
#[derive(Clone, Copy, Debug)]
pub struct Moment {
    pub mono: Instant,
    pub wall: SystemTime,
}

impl Moment {
    pub fn now() -> Moment {
        Moment {
            mono: Instant::now(),
            wall: SystemTime::now(),
        }
    }

    /// How long since `earlier`: the larger of the two clocks' answers.
    fn since(&self, earlier: &Moment) -> Duration {
        let mono = self.mono.saturating_duration_since(earlier.mono);
        let wall = self.wall.duration_since(earlier.wall).unwrap_or_default();
        mono.max(wall)
    }
}

/// What opens the lock.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum Need {
    /// The password alone: within the sign-in's 12 hours.
    Password,
    /// A full sign-in, the password and the authenticator code: after the 12 hours, when the password alone opens it
    /// while no server can complete a sign-in (main.rs); or after too many wrong passwords here, when it doesn't.
    SignIn,
    /// The server ended the session: a full sign-in only.
    SignedOut,
}

/// Why the window locked, for the lock to say.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum Why {
    Idle,
    /// The app started with a sign-in kept.
    Started,
    /// The clinician asked.
    Asked,
    /// The sign-in's 12 hours are over.
    Longest,
    /// Too many wrong passwords were typed here (ehr_desktop::unlock).
    WrongPasswords,
    /// The server ended the session.
    Ended,
    /// The sign-in's 30 days are over, by this computer's clock: the offline copy is closed until a full sign-in.
    OfflineEnded,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum State {
    /// No sign-in is kept: there is nothing to lock.
    SignedOut,
    Unlocked,
    /// `last`: the sign-in's 12-hour end comes in `left`; otherwise the idle end.
    Warning {
        last: bool,
        left: Duration,
    },
    Locked {
        need: Need,
        why: Why,
    },
}

pub struct Lock {
    times: Times,
    /// When the sign-in was made, by the wall clock; none while signed out.
    signed_in_at: Option<SystemTime>,
    /// The last activity.
    last: Moment,
    /// Since when a recording has been held, if one is.
    recording: Option<Moment>,
    locked: Option<(Need, Why)>,
    /// The server says the sign-in's 12 hours are over.
    server_ended: bool,
    /// The 12-hour end has locked the window, which it does once a sign-in.
    ended: bool,
    /// The server ended the session: the window locks as soon as no recording holds it off.
    session_over: bool,
    /// Until when the offline copy may be opened without a sign-in (the session's offlineAccess): after it, the window
    /// locks as soon as no recording holds it off, for a full sign-in only.
    offline_until: Option<SystemTime>,
    /// The 12-hour warning was dismissed, and isn't shown again for this sign-in.
    last_warning_dismissed: bool,
    /// How many times the window has locked, opened or changed what opens it: the page drops a view of the lock older
    /// than one it has already shown (step 19a's final review).
    generation: u64,
}

impl Lock {
    /// Locked from the start whenever a sign-in is kept: for the password, or past the 12 hours for a full sign-in.
    pub fn new(
        times: Times,
        signed_in_at: Option<SystemTime>,
        server_ended: bool,
        now: Moment,
    ) -> Lock {
        let mut lock = Lock {
            times,
            signed_in_at,
            last: now,
            recording: None,
            locked: None,
            server_ended,
            ended: false,
            session_over: false,
            offline_until: None,
            last_warning_dismissed: false,
            generation: 0,
        };
        if signed_in_at.is_some() {
            lock.ended = lock.past_longest(now);
            lock.set_locked(Some(if lock.ended {
                (Need::SignIn, Why::Longest)
            } else {
                (Need::Password, Why::Started)
            }));
        }
        lock
    }

    /// Where the lock stands now, locking the window when it's time.
    pub fn tick(&mut self, now: Moment) -> State {
        if self.signed_in_at.is_none() {
            return State::SignedOut;
        }
        if let Some((need, _)) = self.locked {
            // A lock asks for more when there is more to ask: a full sign-in once the 12 hours are over, and nothing
            // but one once the session or its 30 days are over.
            if need != Need::SignedOut && self.session_over {
                self.set_locked(Some((Need::SignedOut, Why::Ended)));
            } else if need != Need::SignedOut && self.offline_over(now) {
                self.set_locked(Some((Need::SignedOut, Why::OfflineEnded)));
            } else if need == Need::Password && self.past_longest(now) {
                self.ended = true;
                self.set_locked(Some((Need::SignIn, Why::Longest)));
            }
            let (need, why) = self.locked.unwrap_or((need, Why::Started));
            return State::Locked { need, why };
        }
        if self.recording_now(now) {
            return State::Unlocked;
        }
        if self.session_over {
            return self.lock(Need::SignedOut, Why::Ended);
        }
        if self.offline_over(now) {
            return self.lock(Need::SignedOut, Why::OfflineEnded);
        }
        if !self.ended && self.past_longest(now) {
            self.ended = true;
            return self.lock(Need::SignIn, Why::Longest);
        }
        let idle_left = self.times.idle.saturating_sub(now.since(&self.last));
        if idle_left.is_zero() {
            let need = if self.ended {
                Need::SignIn
            } else {
                Need::Password
            };
            return self.lock(need, Why::Idle);
        }
        if let Some(left) = self.last_left(now)
            && left <= self.times.warning
            && !self.last_warning_dismissed
        {
            return State::Warning { last: true, left };
        }
        if idle_left <= self.times.warning {
            return State::Warning {
                last: false,
                left: idle_left,
            };
        }
        State::Unlocked
    }

    /// A key, a click or tap, the wheel or typing in the window. While the 12-hour warning is showing, it closes it for
    /// good, as the web's "Keep working" does; it moves nothing while the window is locked.
    pub fn activity(&mut self, now: Moment) {
        if self.locked.is_some() {
            return;
        }
        if self
            .last_left(now)
            .is_some_and(|left| left <= self.times.warning)
        {
            self.last_warning_dismissed = true;
        }
        self.last = now;
    }

    /// A recording began, or ended: either way it's activity.
    pub fn recording(&mut self, on: bool, now: Moment) {
        self.recording = on.then_some(now);
        if self.locked.is_none() {
            self.last = now;
        }
    }

    /// The clinician locked the window.
    pub fn lock_now(&mut self, now: Moment) {
        if self.signed_in_at.is_some() && self.locked.is_none() {
            let need = if self.ended || self.past_longest(now) {
                Need::SignIn
            } else {
                Need::Password
            };
            self.set_locked(Some((need, Why::Asked)));
        }
    }

    /// Too many wrong passwords here: only a full sign-in opens the lock now.
    pub fn require_sign_in(&mut self) {
        if self.locked.is_some_and(|(need, _)| need == Need::Password) {
            self.set_locked(Some((Need::SignIn, Why::WrongPasswords)));
        }
    }

    /// The lock opened: with the password, or a full sign-in (`signed_in`).
    pub fn unlocked(&mut self, now: Moment) {
        self.set_locked(None);
        self.last = now;
    }

    /// A sign-in with both factors, from the page's form or the lock's: its 12 hours start now.
    pub fn signed_in(&mut self, at: SystemTime, now: Moment) {
        *self = Lock {
            recording: self.recording,
            generation: self.generation + 1,
            ..Lock::new(self.times, None, false, now)
        };
        self.signed_in_at = Some(at);
    }

    pub fn signed_out(&mut self, now: Moment) {
        *self = Lock {
            generation: self.generation + 1,
            ..Lock::new(self.times, None, false, now)
        };
    }

    /// Until when the kept session lets the offline copy be opened without a sign-in: given at start and after each
    /// sign-in with both factors (main.rs); none for a session from a server too old to say.
    pub fn offline_until(&mut self, until: Option<SystemTime>) {
        self.offline_until = until;
    }

    /// The server refused the token because the sign-in's 12 hours are over.
    pub fn server_ended_sign_in(&mut self) {
        self.server_ended = true;
    }

    /// The server refused the token: the session is over.
    pub fn server_ended_session(&mut self) {
        self.session_over = true;
    }

    pub fn is_locked(&self) -> bool {
        self.locked.is_some()
    }

    pub fn need(&self) -> Option<Need> {
        self.locked.map(|(need, _)| need)
    }

    /// The sign-in's 12 hours are over: a full sign-in is needed as soon as a server can complete one.
    pub fn sign_in_ended(&self, now: Moment) -> bool {
        self.signed_in_at.is_some() && (self.ended || self.past_longest(now))
    }

    /// The server ended the session; the window is locked, or will be once the recording ends.
    pub fn session_over(&self) -> bool {
        self.session_over
    }

    pub fn generation(&self) -> u64 {
        self.generation
    }

    fn set_locked(&mut self, locked: Option<(Need, Why)>) {
        if self.locked != locked {
            self.locked = locked;
            self.generation += 1;
        }
    }

    fn lock(&mut self, need: Need, why: Why) -> State {
        self.set_locked(Some((need, why)));
        State::Locked { need, why }
    }

    fn past_longest(&self, now: Moment) -> bool {
        self.server_ended
            || self.signed_in_at.is_some_and(|at| {
                now.wall.duration_since(at).unwrap_or_default() >= self.times.longest
            })
    }

    /// Until the 12-hour end, while it is still to come.
    fn last_left(&self, now: Moment) -> Option<Duration> {
        if self.ended || self.server_ended {
            return None;
        }
        let end = self.signed_in_at? + self.times.longest;
        Some(end.duration_since(now.wall).unwrap_or_default())
    }

    /// The sign-in's 30 days are over, by this computer's clock.
    fn offline_over(&self, now: Moment) -> bool {
        self.signed_in_at.is_some() && self.offline_until.is_some_and(|until| now.wall >= until)
    }

    /// A recording is being made, and holds the lock off (and an update's restart, updates.rs).
    pub fn recording_now(&self, now: Moment) -> bool {
        self.recording
            .is_some_and(|since| now.since(&since) < RECORDING_HOLD)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const MINUTE: Duration = Duration::from_secs(60);

    /// A moment `after` the start on both clocks.
    fn at(start: Moment, after: Duration) -> Moment {
        Moment {
            mono: start.mono + after,
            wall: start.wall + after,
        }
    }

    /// Signed in at `start` and unlocked.
    fn open(start: Moment) -> Lock {
        let mut lock = Lock::new(Times::STANDARD, Some(start.wall), false, start);
        lock.unlocked(start);
        lock
    }

    /// TC-UC7-09 (proposed FR-68): a kept sign-in starts locked, for the password; with none kept there's nothing to lock.
    #[test]
    fn the_app_starts_locked_whenever_a_sign_in_is_kept() {
        let start = Moment::now();
        let mut kept = Lock::new(Times::STANDARD, Some(start.wall), false, start);
        let mut none = Lock::new(Times::STANDARD, None, false, start);

        assert_eq!(
            kept.tick(start),
            State::Locked {
                need: Need::Password,
                why: Why::Started
            }
        );
        assert_eq!(none.tick(at(start, 13 * 60 * MINUTE)), State::SignedOut);
        let yesterday = Lock::new(
            Times::STANDARD,
            Some(start.wall - 13 * 60 * MINUTE),
            false,
            start,
        )
        .tick(start);
        assert_eq!(
            yesterday,
            State::Locked {
                need: Need::SignIn,
                why: Why::Longest
            }
        );
    }

    /// TC-UC7-13 (proposed FR-69, Tahseen's 14b): when the sign-in's 30 days pass by this computer's clock, the window
    /// locks for a full sign-in only, as soon as no recording holds it off; a lock for the password asks for one too;
    /// and a kept sign-in started past them opens on that lock.
    #[test]
    fn the_offline_copy_locks_for_a_full_sign_in_when_its_30_days_pass() {
        let start = Moment::now();
        let offline_ended = State::Locked {
            need: Need::SignedOut,
            why: Why::OfflineEnded,
        };
        let mut lock = open(start);
        lock.offline_until(Some(start.wall + 10 * MINUTE));

        lock.recording(true, at(start, 9 * MINUTE));
        assert_eq!(lock.tick(at(start, 10 * MINUTE)), State::Unlocked);
        lock.recording(false, at(start, 11 * MINUTE));
        assert_eq!(lock.tick(at(start, 11 * MINUTE)), offline_ended);
        lock.unlocked(at(start, 12 * MINUTE));
        assert_eq!(lock.tick(at(start, 12 * MINUTE)), offline_ended);

        let mut idle = open(start);
        idle.offline_until(Some(start.wall + 20 * MINUTE));
        assert!(matches!(
            idle.tick(at(start, 15 * MINUTE)),
            State::Locked {
                need: Need::Password,
                ..
            }
        ));
        assert_eq!(idle.tick(at(start, 20 * MINUTE)), offline_ended);

        let mut kept = Lock::new(Times::STANDARD, Some(start.wall), false, start);
        kept.offline_until(Some(start.wall - MINUTE));
        assert_eq!(kept.tick(start), offline_ended);
        let mut signed_out = Lock::new(Times::STANDARD, None, false, start);
        signed_out.offline_until(Some(start.wall - MINUTE));
        assert_eq!(signed_out.tick(start), State::SignedOut);
    }

    /// TC-UC7-09: warned at 13 idle minutes, with 2 left; any activity starts the 15 minutes again; locked at 15.
    #[test]
    fn it_warns_at_13_idle_minutes_and_locks_at_15() {
        let start = Moment::now();
        let mut lock = open(start);

        assert_eq!(lock.tick(at(start, 12 * MINUTE)), State::Unlocked);
        assert_eq!(
            lock.tick(at(start, 13 * MINUTE)),
            State::Warning {
                last: false,
                left: 2 * MINUTE
            }
        );
        lock.activity(at(start, 14 * MINUTE));
        assert_eq!(lock.tick(at(start, 26 * MINUTE)), State::Unlocked);
        assert_eq!(
            lock.tick(at(start, 29 * MINUTE)),
            State::Locked {
                need: Need::Password,
                why: Why::Idle
            }
        );
        lock.activity(at(start, 30 * MINUTE));
        assert!(lock.is_locked(), "activity doesn't open it");
        lock.unlocked(at(start, 31 * MINUTE));
        assert_eq!(lock.tick(at(start, 43 * MINUTE)), State::Unlocked);
    }

    /// TC-UC7-09: a recording is never warned over or locked, and counts as activity; a hold the page never ended
    /// lapses after 5 minutes, the idle time counting from when it began.
    #[test]
    fn a_recording_holds_the_lock_off_and_counts_as_activity() {
        let start = Moment::now();
        let mut lock = open(start);

        lock.recording(true, at(start, 14 * MINUTE));
        assert_eq!(lock.tick(at(start, 15 * MINUTE)), State::Unlocked);
        lock.recording(false, at(start, 15 * MINUTE));
        assert_eq!(lock.tick(at(start, 27 * MINUTE)), State::Unlocked);
        assert!(matches!(
            lock.tick(at(start, 30 * MINUTE)),
            State::Locked { .. }
        ));

        let mut stuck = open(start);
        stuck.recording(true, at(start, MINUTE));
        assert_eq!(stuck.tick(at(start, 5 * MINUTE)), State::Unlocked);
        assert_eq!(
            stuck.tick(at(start, 16 * MINUTE)),
            State::Locked {
                need: Need::Password,
                why: Why::Idle
            }
        );
    }

    /// TC-UC7-09: time asleep counts, through the wall clock, though Instant stood still; setting the clock back gains
    /// nothing, through Instant.
    #[test]
    fn time_asleep_counts_and_a_clock_set_back_gains_nothing() {
        let start = Moment::now();
        let mut slept = open(start);
        let woke = Moment {
            mono: start.mono + Duration::from_secs(1),
            wall: start.wall + 16 * MINUTE,
        };
        assert!(matches!(slept.tick(woke), State::Locked { .. }));

        let mut set_back = open(start);
        let later = Moment {
            mono: start.mono + 16 * MINUTE,
            wall: start.wall - 60 * MINUTE,
        };
        assert!(matches!(set_back.tick(later), State::Locked { .. }));
    }

    /// TC-UC7-09: at 11 h 58 min the 12-hour warning shows once, and activity closes it for good; at 12 hours the
    /// window locks for a full sign-in however active, though not over a recording.
    #[test]
    fn the_12_hour_end_warns_once_then_locks_for_a_full_sign_in() {
        let start = Moment::now();
        let mut lock = open(start);
        let keep_busy = |lock: &mut Lock, until: Duration| {
            let mut time = Duration::ZERO;
            while time < until {
                time += 10 * MINUTE;
                lock.activity(at(start, time.min(until)));
            }
        };

        keep_busy(&mut lock, 11 * 60 * MINUTE + 57 * MINUTE);
        assert_eq!(
            lock.tick(at(start, 11 * 60 * MINUTE + 58 * MINUTE)),
            State::Warning {
                last: true,
                left: 2 * MINUTE
            }
        );
        lock.activity(at(start, 11 * 60 * MINUTE + 58 * MINUTE));
        assert_eq!(
            lock.tick(at(start, 11 * 60 * MINUTE + 59 * MINUTE)),
            State::Unlocked
        );
        lock.recording(true, at(start, 11 * 60 * MINUTE + 59 * MINUTE));
        assert_eq!(lock.tick(at(start, 12 * 60 * MINUTE)), State::Unlocked);
        lock.recording(false, at(start, 12 * 60 * MINUTE + MINUTE));
        assert_eq!(
            lock.tick(at(start, 12 * 60 * MINUTE + MINUTE)),
            State::Locked {
                need: Need::SignIn,
                why: Why::Longest
            }
        );
        assert!(lock.sign_in_ended(at(start, 12 * 60 * MINUTE + MINUTE)));
    }

    /// TC-UC7-09: opened past the 12 hours with the password alone (no server could complete a sign-in), the window
    /// isn't locked again each time a server says the sign-in has ended; its next idle lock asks for a full sign-in;
    /// a full sign-in starts 12 new hours.
    #[test]
    fn after_the_12_hour_lock_opens_offline_it_locks_again_only_when_idle() {
        let start = Moment::now();
        let mut lock = open(start);
        let end = at(start, 12 * 60 * MINUTE);
        assert!(matches!(lock.tick(end), State::Locked { .. }));

        lock.unlocked(end);
        lock.server_ended_sign_in();
        assert_eq!(lock.tick(at(end, MINUTE)), State::Unlocked);
        assert!(lock.sign_in_ended(at(end, MINUTE)));
        assert_eq!(
            lock.tick(at(end, 15 * MINUTE)),
            State::Locked {
                need: Need::SignIn,
                why: Why::Idle
            }
        );

        let again = at(end, 16 * MINUTE);
        lock.signed_in(again.wall, again);
        assert_eq!(lock.tick(again), State::Unlocked);
        assert!(!lock.sign_in_ended(again));
    }

    /// TC-UC7-09: a server saying the 12 hours are over before this computer's clock does locks the window once, for a
    /// full sign-in; and a lock for the password asks for more once the 12 hours pass.
    #[test]
    fn the_servers_word_on_the_12_hours_counts_and_a_waiting_lock_asks_for_more() {
        let start = Moment::now();
        let mut lock = open(start);
        lock.server_ended_sign_in();
        assert_eq!(
            lock.tick(at(start, MINUTE)),
            State::Locked {
                need: Need::SignIn,
                why: Why::Longest
            }
        );

        let mut waiting = open(start);
        waiting.lock_now(at(start, MINUTE));
        assert_eq!(waiting.need(), Some(Need::Password));
        assert_eq!(
            waiting.tick(at(start, 12 * 60 * MINUTE)),
            State::Locked {
                need: Need::SignIn,
                why: Why::Longest
            }
        );
    }

    /// TC-UC7-09: the server ending the session locks the window for a full sign-in, after a recording if one is being
    /// made, and turns a lock already up into one.
    #[test]
    fn a_session_the_server_ends_locks_for_a_full_sign_in() {
        let start = Moment::now();
        let mut lock = open(start);
        lock.recording(true, at(start, MINUTE));
        lock.server_ended_session();
        assert_eq!(lock.tick(at(start, 2 * MINUTE)), State::Unlocked);
        lock.recording(false, at(start, 2 * MINUTE));
        assert_eq!(
            lock.tick(at(start, 2 * MINUTE)),
            State::Locked {
                need: Need::SignedOut,
                why: Why::Ended
            }
        );

        let mut idle = open(start);
        assert!(matches!(
            idle.tick(at(start, 15 * MINUTE)),
            State::Locked { .. }
        ));
        idle.server_ended_session();
        assert_eq!(
            idle.tick(at(start, 16 * MINUTE)),
            State::Locked {
                need: Need::SignedOut,
                why: Why::Ended
            }
        );

        idle.signed_out(at(start, 17 * MINUTE));
        assert_eq!(idle.tick(at(start, 60 * MINUTE)), State::SignedOut);
    }

    /// TC-UC7-09: too many wrong passwords here turn a lock for the password into one for a full sign-in, which a full
    /// sign-in opens; a lock already asking for more stays as it is.
    #[test]
    fn too_many_wrong_passwords_take_a_full_sign_in() {
        let start = Moment::now();
        let mut lock = open(start);
        lock.lock_now(start);
        lock.require_sign_in();
        assert_eq!(
            lock.tick(at(start, MINUTE)),
            State::Locked {
                need: Need::SignIn,
                why: Why::WrongPasswords
            }
        );
        lock.signed_in(at(start, 2 * MINUTE).wall, at(start, 2 * MINUTE));
        assert_eq!(lock.tick(at(start, 2 * MINUTE)), State::Unlocked);

        let mut ended = open(start);
        ended.server_ended_session();
        ended.tick(at(start, MINUTE));
        ended.require_sign_in();
        assert_eq!(ended.need(), Some(Need::SignedOut));
    }

    /// Step 19a's final review: every lock, opening and change of what opens it moves the generation on, sign-ins and
    /// sign-outs included, so the page can tell a view computed before a lock from one after it.
    #[test]
    fn each_lock_and_opening_moves_the_generation_on() {
        let start = Moment::now();
        let mut lock = Lock::new(Times::STANDARD, Some(start.wall), false, start);
        let mut seen = vec![lock.generation()];
        lock.unlocked(start);
        seen.push(lock.generation());
        lock.tick(at(start, 15 * MINUTE));
        seen.push(lock.generation());
        lock.tick(at(start, 16 * MINUTE));
        assert_eq!(
            lock.generation(),
            seen[2],
            "a tick that changes nothing moves nothing"
        );
        lock.server_ended_session();
        lock.tick(at(start, 16 * MINUTE));
        seen.push(lock.generation());
        lock.signed_in(at(start, 17 * MINUTE).wall, at(start, 17 * MINUTE));
        seen.push(lock.generation());
        lock.signed_out(at(start, 18 * MINUTE));
        seen.push(lock.generation());

        assert!(seen.windows(2).all(|pair| pair[0] < pair[1]), "{seen:?}");
    }

    /// The times for checking by hand are only ever shorter, the warning in proportion.
    #[test]
    fn shortened_times_are_never_longer() {
        let short = Times::shortened(Some(Duration::from_secs(180)), Some(10 * MINUTE));
        assert_eq!(
            short,
            Times {
                idle: Duration::from_secs(180),
                warning: Duration::from_secs(24),
                longest: 10 * MINUTE
            }
        );
        assert_eq!(
            Times::shortened(Some(16 * MINUTE), Some(13 * 60 * MINUTE)),
            Times::STANDARD
        );
        assert_eq!(
            Times::shortened(Some(Duration::ZERO), None),
            Times::STANDARD
        );
    }
}
