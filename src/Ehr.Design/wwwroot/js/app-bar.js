// On a narrow window the staff app bar's links are a row of their own that scrolls sideways (ehr.css). A link reached
// from the keyboard is brought into that row's view, as one reached by a swipe is: the browser doesn't always scroll it
// there by itself. Wired here rather than in an attribute, so a strict CSP can be added later.
"use strict";

document.addEventListener("focusin", (event) => {
    if (event.target instanceof Element && event.target.matches(".sc-appbar__nav a")) {
        event.target.scrollIntoView({ block: "nearest", inline: "nearest" });
    }
});
