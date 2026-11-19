# SwiftChart brand kit

Everything needed to brand SwiftChart: the guide below, the tokens, fonts, logos, app icons and reference components. The apps carry almost no styling yet; this is what to apply to each of them.

## In this folder

| Path | What it is |
| --- | --- |
| `README.md` | This file: the folder guide, then the brand guide. |
| `tokens.json` | Every token: colors for the light and dark themes, type families and styles, spacing, radius, shadows and sizes. Each token's `usage` note says where it goes and the contrast it reaches. The source of truth. |
| `tokens.css` | The same tokens as CSS custom properties, `@font-face` rules and one class per type style. Light by default, dark with `[data-theme="dark"]` or the system setting. Font URLs are relative to this file. |
| `fonts/`, `licenses/` | Sora, IBM Plex Sans and IBM Plex Mono (Latin, WOFF2) and their SIL Open Font Licenses; React's MIT license for the previews. |
| `assets/Logos/` | The lockup (color and reversed PNG) and the mark (SVG). Read its README before using them. |
| `assets/App-icons/` | App icons (SVG and 1024px PNG), the 180px Apple touch icon, the 32px favicon, the macOS-grid version, and `desktop/`: the five files the desktop app's `tauri.conf.json` lists, under the same names. |
| `assets/Originals/` | The logo artwork as it appears in the course deck, for reference only. |
| `components/` | Ten reference components. Each folder has a README (when and how to use it, its states and its markup), `preview.html` (open it in a browser; add `?theme=dark`) and `light.png` / `dark.png` renders. `bundle.css` holds their classes (prefix `sc-`), which the apps should reproduce in their own markup. `bundle.js` and `index.d.ts` are the React previews and their props; the apps don't use React. |
| `components/index.html` | All the previews on one page, with a light/dark switch. |
| `cover-light.png`, `cover-dark.png` | The kit's cover. |
| `CLAUDE-CODE-PROMPT.md` | The prompt for implementing the brand across the repo with Claude Code. |

## Using it in the code

- Load `tokens.css` (or generate the same custom properties from `tokens.json`) before any other stylesheet, and serve the fonts from the apps themselves: the on-site server and the desktop app must work with no internet.
- Build components from the token variables only, never raw hex values, so both themes come for free. `components/bundle.css` shows every state in that form.
- Follow the system's light or dark setting by default; `[data-theme]` on `<html>` overrides it.
- Where to apply it: the shared stylesheet both web apps load (`src/Ehr.Design/wwwroot/css/ehr.css`), the desktop app's (`src/Ehr.Desktop/frontend/app.css`), and the desktop icons (`src/Ehr.Desktop/src-tauri/icons/`, replaced by `assets/App-icons/desktop/`).

Components added to the shared stylesheet since the kit, in its style:

- `sc-btn--compact` as a link above a chart list ("Add allergy"), and the "Change" link after an entry (`sc-list__actions`): entries are changed or given another status, never deleted, so no list has a delete action.
- `sc-acting`: the band under the portal's app bar while someone is looking at another person's record as their proxy. An info notice at full width that names whose record it is, with the button back to their own beside it. It is on every page, so nobody has to remember whose record they opened.
- `sc-thread` and `sc-message`: a conversation, oldest message first, each in its own bordered block with who and when above the text. The reader's own messages are set in from the left with an accent bar (`sc-message--mine`); every message also says who wrote it, so the bar is never the only sign. To a patient, staff are "Your care team" and they are "You".
- `sc-appbar__count`: a count beside an app bar link, as on "Inbox": mono, navy on mint, shown only when it isn't zero, and read out as "Inbox, 2 to do".
- `sc-list__actions`: a record line's own actions, as plain links after the line's text, such as an active order's "Change" and "Discontinue…". Links, not buttons, because each opens a page; a destructive one ends in "…" and its page's button is the danger button, which names the action ("Discontinue order").

---

# Brand guide

SwiftChart is an electronic health record for a small clinic: a clinician web app on the clinic's own server, a read-only copy of it off-site, a patient portal, a desktop app that keeps working offline, and dictation that proposes note fields for a clinician to review. This system is the brand for all of them. The apps carry almost no styling yet, so this is the guide to apply as each one is built out. Each surface below starts from the same mark, the same two colors and the same three typefaces, then tunes density and tone for who is using it.

The brand in one line: **calm, fast, exact**. A clinician is busy and a patient may be worried, so screens are quiet, text is plain, and nothing decorative competes with the record.

## The logo

The mark is two half-discs, offset: navy (`brand-navy`) on top with its flat edge down, mint (`brand-mint`) below with its flat edge up, separated by a thin gap. The wordmark "SwiftChart" sits to its right in a heavy geometric sans.

- **Lockup** (`assets/Logos/swiftchart-lockup.png`) on white, `surface-subtle` or any light ground. **Reversed lockup** (`swiftchart-lockup-reversed.png`: white wordmark and upper disc, mint lower disc) on `brand-navy` or `appbar`.
- **Mark alone** (`swiftchart-mark.svg`, `swiftchart-mark-reversed.svg`) where the name is already on screen or space is tight: favicons, the desktop title bar, loading states.
- **Round badge** (`assets/Originals/deck-badge-on-navy.png`): the mark above the wordmark in a white circle with a mint ring. For title slides, stickers and print only; the apps never use it.
- **App icon** (`assets/App-icons/`): the dark icon (navy square, reversed mark) is the default for the desktop app, the web apps' home-screen icon and the favicon. The light icon is for dark docks and dark slides only.
- **Clear space**: keep empty space around the lockup equal to the height of one half-disc on every side. **Minimum size**: the lockup at 96px wide; the mark at 16px tall.
- The wordmark is artwork. Use the lockup files; never retype "SwiftChart" in a font to stand in for it. In running text the name is plain text: "SwiftChart", one word, capital S and C.
- Don't recolor the mark beyond its two versions, swap the halves, close the gap, rotate it, outline it, add shadows or gradients, or put it on photos or busy backgrounds. The colored gradients in the course slide template belong to that template, not to SwiftChart: put the logo on a white or navy card, as slide 8 of the deck does.

## Voice and content

Write like a careful colleague: short sentences, plain words, no exclamation marks, no emoji. Say what happened, then what the person can do. Sentence case everywhere ("Sign note…", not "Sign Note"). Address the person as "you"; the system never says "I".

Copy already in the apps that sets the tone:

- Off-site notice: "Read-only: the clinic's server can't be reached. You can look at records, but the newest changes may not appear yet, and nothing can be saved until it's back."
- Dictation offline: "Dictation needs the clinic's server, which can't be reached right now. You can still type the note once it's back."
- Dictation notice: "Nothing is saved: each proposed field waits for your review, and orders and allergy changes are never filled in from dictation."
- Emergency access: "Emergency access: this patient is not on your panel. Your access and the reason you gave are recorded for review."
- Buttons name the action exactly: "Sign note…", "Confirm and sign note", "Open the chart", "Send a new link", "Keep mine". Never "OK", "Submit" or "Yes".
- Status words are short and literal: "Signed · read-only", "Awaiting review", "Left empty", "Not sent yet", "Mine (not saved)", "Theirs (saved)".

Rules:

- **A notice about a state starts with the state**, then a colon: "Read-only:", "Emergency access:". The word carries the meaning; the color only supports it.
- **Dictation proposes; the clinician decides.** Say "proposed" and "awaiting your review", never "diagnosed", "decided" or "filled in for you". Say what was not carried over and why ("dictation never places orders or changes the allergy list").
- **Clinician screens can be terse**, using MRN and standard clinical abbreviations. **The portal is for patients**: spell labels out ("Medical record number", "Date of birth"), explain results in plain language, and never show internal IDs or audit details.
- **Dates** in clinician views and tables are `YYYY-MM-DD`, with 24-hour times (`2026-11-15 14:05`). The portal writes a result's date out for patients ("Nov 2, 2026"). Say "UTC" when a time is in UTC.
- **Names** in clinician views are charted "Family, Given" ("Rivera, Ana"); the portal greets patients by given name.

## Color

Two brand colors, both from the logo: `brand-navy` (#0E1B2C) and `brand-mint` (#5FD4BA). Navy carries the brand; mint is a small accent that never carries text on a light ground (1.8:1 on white). Where mint has to read as text or a focus ring in the light theme, use `brand-teal` (#0B6E5C), the same hue darkened.

- Text is `ink` (body, headings) or `ink-muted` (metadata, hints) on `surface`, `surface-subtle` or `surface-raised`. Links and accent text are `accent-ink`.
- One primary action per view, filled with `primary` and labeled in `on-primary`. That is navy in light and mint in dark.
- `appbar` stays dark in both themes, so the reversed lockup always sits on it.
- Status: `danger` for errors, high-criticality allergies, the offline dot and emergency access; `warning` for read-only and pending states; `success` for saved, signed, synced and online; `info` for neutral system news. Each has a `-soft` background for notices and chips. `success` and `danger` are close in lightness, so a status is never shown by color alone: it always has a word, and on dots the word sits beside the dot.
- Transcript highlights: `highlight-unclear` marks passages the speech model was unsure of; `highlight-source` marks the words a proposed field came from. Audit rows that need review use `row-flagged`.
- Two themes, Light and Dark, with the same tokens. Follow the operating system's setting (`color-scheme: light dark`). The dark theme's ground is `brand-navy` itself, so the brand sets the mood even there.

## Typography

Three families, all open-source (SIL Open Font License) and served from the apps themselves, never from a font CDN: the on-site server and the desktop app must work with no internet. The files are in `fonts/` (Latin subsets, WOFF2). They load with `font-display: swap`, so text shows at once in the fallback font, and no page preloads them: the patient search page must fetch nothing ahead (a test that guards patient information holds it to that), so no page does. Sora and IBM Plex Sans are the course deck's own pairing.

- **Sora** (`--font-display`) for headings and display text only: `display`, `title-1`, `title-2`, `title-3`. Never for form labels, tables or body text.
- **IBM Plex Sans** (`--font-sans`) for everything people read and operate: `body`, `body-strong`, `body-sm`, `label`, `caption`; and `portal-body` (18px) as the portal's default body size.
- **IBM Plex Mono** (`--font-mono`) for things people compare or type character by character: `mono-id` (MRNs, audit row IDs, table timestamps), `mono-shortcut` (keyboard shortcuts and quick-text triggers such as `.nad`) and `mono-code` (authenticator keys and recovery codes, letter-spaced). An authenticator key is shown in groups of four; a recovery code is shown exactly as it was issued, since that is what the person types back.
- One `title-1` per page. Headings follow the page outline (h1 to h3) and are never chosen just for their size.

## Layout and spacing

Spacing comes from a 4px base (`space-1` to `space-16`). Clinician screens are dense: `space-2` to `space-4` inside panels, `control-height` (36px) controls, `body-sm` in tables. The portal breathes: `space-6` to `space-12`, `control-height-portal` (44px) controls, `portal-body` text and a single column at `width-reading`. Forms sit in one column at `width-form`, and clinician pages max out at `width-content`.

The patient is always visible. On every clinician screen about a patient, the patient banner sits directly under the app bar and stays put while the page scrolls.

## Shape, borders and depth

Corners are small and precise: `radius-sm` for controls and tags, `radius-md` for panels, notices and the banner, and `radius-lg` only for dialogs and portal cards. `radius-full` is only for status dots. Separate things with `line` hairlines and `surface-subtle` panels, not shadows. Shadows (`shadow-float`, `shadow-dialog`) only lift layers that float: menus, autocomplete lists and dialogs.

The mark's motif, two offset half-discs, is for brand moments only: sign-in, the portal welcome, empty states, the loading state and slides. Don't put it inside working screens.

## Iconography

SwiftChart has no icon set of its own yet, and the apps use none today. When icons arrive, use **Lucide** (ISC license): outline icons at 20px with a 1.75 stroke, colored `currentColor` so they take the text color. Ship the SVGs inside the apps; don't load them from a CDN. An icon always sits beside its word, except in a toolbar, where it has a tooltip and an accessible name. Status icons follow their status color, never the brand colors.

## Focus, motion and accessibility

- Every interactive element shows `focus` as a 2px solid outline, offset 2px, when focused from the keyboard. Never remove it.
- Clinician screens are keyboard-first (NFR-12): every common task can be done without a mouse, focus starts in the first field, and shortcuts the apps already have ("/" for patient search, Alt+Shift+D to dictate) appear on their controls in `mono-shortcut` and in `aria-keyshortcuts`.
- Motion is short and functional: 120ms to 200ms ease-out for opening menus, showing notices and updating rows. Nothing moves in the record itself. Respect `prefers-reduced-motion`.
- A trend chart (`sc-trend`) is never the only way to read its numbers: a sentence under it says what it shows, and a table has every value. The line and points are `ink`, the reference range is a band of `success-soft`, and a value outside the range is a diamond filled with `warning`, so shape tells it apart as well as color. Charts are SVG drawn by the server, colored by the stylesheet's classes.
- Text meets WCAG 2 AA (4.5:1, or 3:1 at 24px and up) on its stated grounds in both themes. Controls, focus rings and status dots meet 3:1. Each color token's note gives its ratios.

## The surfaces

### Clinician web app (on-site)

The main product, for clinicians at the clinic. App bar: `appbar` with the reversed lockup on the left, then the main navigation (Patients, with its "/" shortcut, and Quick text), and on the right the signed-in name and "Sign out". The current nav item gets a 2px `accent` underline. Under it, the patient banner on every patient page: name, MRN, date of birth and gender. Where the page already reads the patient's allergies (the chart and the order form), the banner shows them too: high-criticality ones as a danger chip, "No known allergies", or "No allergy information recorded" in `ink-muted`. On the other patient pages (notes, dictation and its review) the banner has no allergy area, rather than a "none recorded" the page hasn't checked. Dense layout (`body-sm` tables, 36px controls) at `width-content`. One primary button per screen: "Sign note…" on the note editor, "Confirm and sign note" on the review page. A signed note shows a "Signed · read-only" tag beside its title.

### Off-site access (cloud copy)

The same app, served from the cloud copy when the clinic is unreachable. It looks the same, with three differences: the app bar shows an "Off-site" tag next to the lockup; the full-width warning notice sits under the app bar on every page ("Read-only: the clinic's server can't be reached. …"); and each save action is taken off the page, with a sentence in its place that says what can't be done and why ("New orders need the clinic's server. Active orders remain readable."). Never leave a gap where an action was; say why it isn't there.

### Patient portal

For patients. Light app bar (`surface-raised`, `line` bottom border) with the full-color lockup, or the reversed lockup in dark. Navigation starts with "My record"; results, appointments and messages join it as they are built. Larger and calmer: `portal-body` text, `display` for the welcome line, 44px controls, cards with `radius-lg` and `space-6` padding, and a single column at `width-reading`. Results lead with one plain-language sentence, with the clinical value and its reference range underneath. Registration, sign-in, "Forgot password" and "Send a new link" sit in a single `width-form` column, with the mark centered above.

### Desktop app (macOS and Windows)

Same tokens and components as the web app, inside a native window. Use the dark app icon in place of the placeholder in `src-tauri/icons`: `assets/App-icons/desktop/` holds the files the build lists, under the same names, and its README gives the command for the macOS ICNS, which is padded to Apple's icon grid. Show the mark (16px) and "SwiftChart" in the window title. Connection status is always visible at the top right: a dot plus the words the app already uses ("Online: <server>", "Offline", "Connecting…", "Signed out"), with "Sync now" beside it. Offline, drafts and recordings are kept on the device, and a line says how many are waiting ("2 draft or recording changes need sync or review."). A sync conflict shows both versions side by side, "Theirs (saved)" and "Mine (not saved)", with "Keep mine" and "Keep theirs"; it never merges silently.

### Dictation

Controls: "Dictate" (Alt+Shift+D) as the primary button, then "Stop" and "Cancel". The status line under them is a live region in `body-sm` ("Recording. Speak your note, then select Stop."), with a `danger` dot while recording. The transcript is a bordered panel: final text in `ink`, words still being recognized in `ink-muted` italic, and unsure passages under `highlight-unclear`, explained once by a notice ("Highlighted passages may be wrong…"). Proposed fields (History, Assessment, Plan) sit one per row, each with its status as a tag ("Awaiting review" warning, "Left empty" and "Not dictated yet" neutral, "Proposing…" info) and any check under it in `warning` text. Selecting or focusing a field paints its source words with `highlight-source`. Orders and allergy changes heard in the dictation are listed under "Not carried over from dictation", never filled in.

### Sensitive items

Some of a chart is more private than the rest: mental-health and substance-use diagnoses, some others by their code, and anything a clinician marks. These are never on screen by accident.

- **Left out, not blurred.** A page doesn't receive sensitive items until someone asks. In their place an info notice says how many are hidden and offers the way to see them: "Sensitive items hidden: 2 diagnoses or medications are not shown. Show sensitive items". The link is a plain link at the end of the notice.
- **Asking is recorded, and the page says so.** Once shown, the notice reads "Sensitive items shown: your view of them is recorded." with "Hide sensitive items". Never show them without that sentence.
- **Tagged where they appear.** Each sensitive entry carries a "Sensitive" tag in the warning tone (`sc-tag sc-tag--warning`), after its text and before its actions.
- **Never in the patient banner, a notification, an email or a page title.** The banner shows allergies only.
- **In a form,** "Sensitive" is a checkbox with a hint under it that says what marking does. Where the system marks an item by rule, the hint says so; the clinician can't clear it.
- **Offline,** the desktop app doesn't keep sensitive items. Where they would be, a muted line says how many aren't kept and where to see them.
- **For a patient's proxy,** sensitive items don't exist unless the patient has allowed them: no count and no notice.

### Emergency access

When a clinician opens a patient who isn't on their panel, a page asks for the reason before the chart opens ("Reason", at least 10 characters; button "Open the chart"). While access lasts, the patient banner carries an "Emergency access" danger tag with the time it ends, and a danger notice under it says that the access and the reason are recorded for review.

### Admin

Admins see "Staff" and "Audit" in the navigation. Audit tables are dense: `mono-id` for times and row IDs, `body-sm` for names and actions, sticky `surface-subtle` headers, and the filters above with a "Show" button. Rows that need review (emergency access) are tinted `row-flagged` and carry a danger tag; the color is never the only signal.

### Email

Account emails (confirming an address, resetting a password) are single-column (`width-reading`) on white: the full-color lockup at 140px wide, a `title-2` heading, `portal-body` text and one navy button with white text. Every email has a plain-text version, and none ever contains clinical details: later message notices only point the patient to the portal.

### Documents and slides

Use Sora for titles and IBM Plex Sans for body text, as the course deck already does. Place the lockup on a white or navy card, never directly on the slide template's gradients. Screenshots of the apps keep their real colors; don't recolor them to match a slide theme.
