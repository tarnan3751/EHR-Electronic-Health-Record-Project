# UC 9 — Transcribe dictated audio

SwiftChart EHR · Samir Mukshe · CS 3354 Software Engineering, Fall 2026

The clinician dictates; the dictation sidecar (`src/Ehr.Dictation/sidecar`) transcribes it with MedASR
as they speak, and the on-prem app's dictation API (`src/Ehr.Web/Api/Dictation/DictationApi.cs`) passes the audio and
the transcript between the page and the sidecar.

Since the expansion plan's step 17 (proposed FR-61, NFR-14 as reworded), the clinician chooses on the Dictate page the
language of each dictation, English first and never remembered. A dictation in Spanish is transcribed by Whisper
(`whisper_asr.py`), passage by passage as each finishes, up to 2 minutes, with a passage not heard as Spanish marked
`other_language` and one whose words look made up `low_confidence`; without Whisper installed, Spanish is refused with
"Spanish dictation isn't installed on this server." The desktop app's offline recordings are always English.

## Test cases and their tests

The numbered test cases are the ones in the SE document (Assignment 4, sections 7.2 and 7.3), with the same IDs. Where a
case has more than one test, the first is the primary one; the others cover the same case at another layer.

| ID | Test case | Requirements | Tests |
|---|---|---|---|
| TC-UC9-01 | Successful flow: dictate and see the transcript as you speak | FR-07, FR-10, FR-32, NFR-14 | `src/Ehr.Dictation/sidecar/tests/test_sessions.py`: `test_chunks_in_order_build_a_live_transcript`; also `tests/Ehr.Web.Tests/DictationApiTests.cs`: `A_clinician_dictates_and_gets_the_fields_as_pending_proposals`, and `DictationPageTests.cs`: `The_page_token_drives_a_whole_dictation_to_pending_fields` |
| TC-UC9-02 | Alternative flow: cancel discards the dictation | FR-13 | `test_sessions.py`: `test_cancel_discards_audio_and_transcript` |
| TC-UC9-03 | Invalid input: audio that isn't audio, or can't be decoded, is refused | FR-10 | `test_sessions.py`: `test_bytes_that_are_not_audio_are_refused_as_not_audio`; also `test_api.py`: `test_bytes_that_are_not_audio_are_refused_at_stop`, and `DictationApiTests.cs`: `Anything_but_audio_is_refused_with_415`, `Audio_that_cannot_be_decoded_is_refused_with_422` |
| TC-UC9-04 | Exceptional condition: the connection drops and the transcript resumes | FR-07, FR-10 | `test_sessions.py`: `test_after_a_dropped_connection_the_transcript_resumes_where_it_left_off`; also `test_a_chunk_sent_again_is_ignored_and_one_that_skips_ahead_is_refused`, and `DictationApiTests.cs`: `A_chunk_out_of_order_is_answered_with_the_one_to_send_next` |
| TC-UC9-05 | NFR-07: dictated words never leave the team's server or reach the logs | NFR-07 | `test_logging.py`: `test_a_dictation_logs_none_of_its_words`; also `test_audio.py`: `test_nothing_is_written_to_disk`, and `DictationApiTests.cs`: `Nothing_is_saved_by_a_whole_dictation` |
| TC-UC9-06 | NFR-14: English dictation accuracy and live-transcript speed | NFR-14, FR-07 | Accuracy: `eval/tests/test_gate.py`: `test_a_score_below_the_accepted_baseline_fails`, and CI's "Check the committed eval results" step (`.github/workflows/ci.yml`), which checks `eval/results/latest.json`. Speed: `test_sessions.py`: `test_a_chunk_is_answered_in_time_while_medasr_is_busy_and_the_next_one_catches_up`, `test_stop_answers_within_its_budget_and_the_result_follows` |
| TC-UC9-07 | Successful flow (step 17): Spanish chosen, the service starts a dictation in Spanish, with 2 minutes, and the transcript comes back in Spanish | proposed FR-61, FR-07, FR-10 | `DictationSpanishTests.cs`: `TC_UC9_07_a_dictation_started_in_spanish_is_one_in_spanish`; also `DictationSpanishBrowserTests.cs`: `TC_UC9_12_spanish_is_chosen_and_dictated_from_the_keyboard`, `test_api.py`: `test_a_dictation_starts_in_english_unless_asked_for_spanish`, `test_sessions.py`: `test_a_dictation_in_spanish_goes_through_whisper_the_spanish_checks_and_proposes_nothing`, `test_a_live_spanish_transcript_has_finished_passages_only`, `test_whisper_asr.py`: `test_a_passage_is_transcribed_greedily_in_spanish_with_nothing_carried_over`, and `CloudForwardingTests.cs`: `A_dictation_started_off_site_in_spanish_is_forwarded_with_its_body_as_sent`. The 2-minute limit (the decision log's step 17, decision 20): `test_sessions.py`: `test_a_dictation_in_spanish_is_refused_past_two_minutes_and_one_in_english_isnt`, `DictationSpanishTests.cs`: `A_dictation_in_spanish_is_refused_past_its_limit`, `DictationSpanishBrowserTests.cs`: `A_dictation_in_spanish_stops_at_its_limit` |
| TC-UC9-08 | Alternative flow (step 17): English is the default and the choice is never remembered; the plain start and the desktop app's offline recordings are English | proposed FR-61 | `DictationSpanishTests.cs`: `TC_UC9_08_english_is_the_default_and_the_plain_and_desktop_starts_are_english`; also `DictationSpanishBrowserTests.cs`: `TC_UC9_08_a_choice_of_spanish_is_never_remembered` |
| TC-UC9-09 | Invalid input (step 17): a language other than "en" or "es" is refused; Spanish without Whisper installed is refused, and the page says "Spanish dictation isn't installed on this server." | proposed FR-61 | `DictationSpanishTests.cs`: `TC_UC9_09_a_language_other_than_en_or_es_is_refused`, `TC_UC9_09_spanish_without_whisper_installed_says_so`; also `DictationSpanishBrowserTests.cs`: `TC_UC9_09_the_page_says_spanish_isnt_installed`, `test_api.py`: `test_any_other_start_is_refused_as_a_bad_language`, `test_spanish_without_whisper_is_refused_as_unavailable`, and `DictationApiTests.cs`: `The_services_refusals_become_the_apis_own` |
| TC-UC9-10 | Exceptional condition (step 17): a passage not heard as Spanish, or whose words Whisper may have made up, is marked, shown and left out of what is sorted | proposed FR-61, FR-10 | `test_whisper_asr.py`: `test_a_passage_not_heard_as_spanish_is_other_language`, `test_a_passage_whisper_may_have_made_up_is_low_confidence`, `test_the_verdict`; also `test_transcriber.py`: `test_the_recognizers_verdict_marks_a_passage_whose_sound_passes`, `test_sessions.py`: `test_a_passage_not_heard_as_spanish_is_marked_and_left_out_of_what_is_sorted`, `DictationSpanishTests.cs`: `TC_UC9_10_a_passage_not_heard_as_spanish_comes_back_marked`, and `DictationSpanishBrowserTests.cs`: `TC_UC9_12_spanish_is_chosen_and_dictated_from_the_keyboard` |
| TC-UC9-11 | NFR-14 as reworded (step 17): Spanish dictation accuracy, and a Spanish live transcript's speed | NFR-14, FR-07 | Accuracy: `eval/tests/test_gate_es.py`: `test_a_spanish_baseline_holds_the_results_to_its_machine_and_scores`, and CI's "Check the committed eval results" step, which checks `eval/results/latest-es.json`. Speed: `test_sessions.py`: `test_a_spanish_chunk_is_answered_in_time_while_whisper_is_busy_and_the_next_one_catches_up`, `test_a_spanish_stop_answers_within_its_budget_and_the_result_follows`; the eval's live chunk times (`latest-es.json`, `metrics.live`) |
| TC-UC9-12 | NFR-12 (step 17): the language chosen and a Spanish dictation made from the keyboard, in real Chromium, in both themes | NFR-12 | `DictationSpanishBrowserTests.cs`: `TC_UC9_12_spanish_is_chosen_and_dictated_from_the_keyboard`, `The_language_choice_fits_every_width_in_both_themes` |

Unprefixed `test_*.py` files are in `src/Ehr.Dictation/sidecar/tests/`, and unprefixed `*Tests.cs` files in
`tests/Ehr.Web.Tests/`.

## Limits

- TC-UC9-04 checks the sidecar's side of resuming: a chunk sent again is ignored, a chunk that skips ahead is told which
  to send, and the transcript carries on. The page's own retry after a dropped request isn't tested in a browser.
- TC-UC9-06's accuracy is the committed eval run, made on a developer Mac (it says so), not on the clinic server. The
  gate has no accepted baseline yet, so "no lower than the baseline" has nothing to compare with until one is accepted.
- TC-UC9-11's accuracy needs the Spanish eval run on audio, which hasn't been made yet: until `eval/results/latest-es.json`
  is committed the gate fails. No test runs Whisper itself: its decoding is tested with stand-ins for its processor and
  model, and is first run on recordings by that eval.
