# UC 9 — Transcribe dictated audio

SwiftChart EHR · Samir Mukshe · CS 3354 Software Engineering, Fall 2026

The clinician dictates; the dictation sidecar (`src/Ehr.Dictation/sidecar`) transcribes it with MedASR
as they speak, and the on-prem app's dictation API (`src/Ehr.Web/Api/Dictation/DictationApi.cs`) passes the audio and
the transcript between the page and the sidecar.

Since the expansion plan's step 17 (proposed FR-61, NFR-14 as reworded), the clinician chooses on the Dictate page the
language of each dictation, English first and never remembered. A dictation in Spanish is transcribed by Whisper
(`whisper_asr.py`), passage by passage as each finishes, up to 2 minutes, with a passage not heard as Spanish marked
`other_language` and one whose words look made up `low_confidence`; without Whisper installed, Spanish is refused with
"Spanish dictation isn't installed on this server." The desktop app's offline recordings are always English. Spanish is
labelled experimental on the Dictate page (the owner's ruling of 2026-11-25): its choice reads "Spanish, experimental
(proposals in English)", and its hint opens with an "Experimental" tag.

A dictation made with English chosen whose transcript has too few English words, as MedASR writes a dictation in
Spanish (words of English and of no language), offers nothing (the owner's ruling of 2026-11-25; `validation.py`): no
field and no proposal, the transcript kept as plain text, the reason `transcript_not_english`, and the page's words "This
dictation doesn't read as English, so no fields were proposed and the transcript is kept as plain text. If you dictated
in Spanish, choose Spanish as the language and dictate it again." Too few is fewer than two thirds of its words found
in an English word list (SCOWL's, to size 50), the reference data's diagnoses (every ICD-10-CM description's words of
four letters or more), the drug names and the reference data's orderables and allergens, the clinical abbreviations and
units the checks read, the standard abbreviations of a history and a physical examination and titles ("HEENT", "NKDA",
"Dr"), or holding a digit; a word made of English words counts too ("nontender", "echocardiogram"). The line is above
the owner's "mostly not English words", the coordinator's more protective reading (the plan's step 17 entry). It holds
at any length. Most short clinical English is offered ("Assessment: hyperlipidemia.", "Abdomen nontender,
nondistended."), and an English dictation with a Spanish phrase in it is checked as ever; but a short English dictation
more than a third of whose words no source holds is refused. Two measurements, below: 2 of the English scripts' 712
gold sentences taken alone are refused, and 19 of 117 common diagnosis words a reviewer tried aren't counted. What is
still refused includes common diagnoses that ICD-10-CM words differently ("Dyslipidemia, atorvastatin.",
"Nephrolithiasis, tamsulosin.", "Costochondritis.", "Onychomycosis."), exam words ("Normocephalic, atraumatic.",
"Hepatosplenomegaly noted.", and "rhonchi", "crepitus", "rales" or "auscultation" in a sentence short enough), a British
term ("Bibasal crackles.", "Salbutamol nebulizer here, then via spacer at home."), and a lone name ("Okonkwo."). The
clinician then sees the same: the transcript kept as plain text and the words pointing at the Spanish
choice; nothing is lost but the proposed fields, and the note can be typed or dictated again. A recorded visit is never
refused so.

A dictation can be up to 3 minutes in English and 2 in Spanish (the decision log's step 17, decision 20), the limit the
service gives each dictation as it starts. While recording, the Dictate page shows the time left beside Stop, counting
down from the limit as the service takes the audio ("1:52 left"), and its status line says a minute ahead that the
dictation stops then, in the Listen page's form ("Dictation stops in a minute, at its 2-minute limit.", 3-minute in
English), and when 30 seconds and 10 seconds are left, and no more often. At the limit the service takes no more audio, and the page stops recording and
shows "The dictation stopped at its 2-minute limit." (3-minute in English), with what to do: what was recorded is kept
and goes on to the review, and the rest can be dictated in another dictation once that review is done. Its status line
says the limit once, as it says the transcript is being finished.

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
| TC-UC9-07 | Successful flow (step 17): Spanish chosen, the service starts a dictation in Spanish, with 2 minutes, and the transcript comes back in Spanish | proposed FR-61, FR-07, FR-10 | `DictationSpanishTests.cs`: `TC_UC9_07_a_dictation_started_in_spanish_is_one_in_spanish`; also `DictationSpanishBrowserTests.cs`: `TC_UC9_12_spanish_is_chosen_and_dictated_from_the_keyboard`, `test_api.py`: `test_a_dictation_starts_in_english_unless_asked_for_spanish`, `test_sessions.py`: `test_a_dictation_in_spanish_goes_through_whisper_the_spanish_checks_and_proposes_nothing`, `test_a_live_spanish_transcript_has_finished_passages_only`, `test_whisper_asr.py`: `test_a_passage_is_transcribed_greedily_in_spanish_with_nothing_carried_over`, and `CloudForwardingTests.cs`: `A_dictation_started_off_site_in_spanish_is_forwarded_with_its_body_as_sent`. The 2-minute limit (the decision log's step 17, decision 20): `test_sessions.py`: `test_a_dictation_in_spanish_is_refused_past_two_minutes_and_one_in_english_isnt`, `DictationSpanishTests.cs`: `A_dictation_in_spanish_is_refused_past_its_limit`, `DictationSpanishBrowserTests.cs`: `TC_UC9_13_the_time_left_counts_down_and_the_dictation_stops_at_its_limit` |
| TC-UC9-08 | Alternative flow (step 17): English is the default and the choice is never remembered; the plain start and the desktop app's offline recordings are English | proposed FR-61 | `DictationSpanishTests.cs`: `TC_UC9_08_english_is_the_default_and_the_plain_and_desktop_starts_are_english`; also `DictationSpanishBrowserTests.cs`: `TC_UC9_08_a_choice_of_spanish_is_never_remembered` |
| TC-UC9-09 | Invalid input (step 17): a language other than "en" or "es" is refused; Spanish without Whisper installed is refused, and the page says "Spanish dictation isn't installed on this server." | proposed FR-61 | `DictationSpanishTests.cs`: `TC_UC9_09_a_language_other_than_en_or_es_is_refused`, `TC_UC9_09_spanish_without_whisper_installed_says_so`; also `DictationSpanishBrowserTests.cs`: `TC_UC9_09_the_page_says_spanish_isnt_installed`, `test_api.py`: `test_any_other_start_is_refused_as_a_bad_language`, `test_spanish_without_whisper_is_refused_as_unavailable`, and `DictationApiTests.cs`: `The_services_refusals_become_the_apis_own` |
| TC-UC9-10 | Exceptional condition (step 17): a passage not heard as Spanish, or whose words Whisper may have made up, is marked, shown and left out of what is sorted | proposed FR-61, FR-10 | `test_whisper_asr.py`: `test_a_passage_not_heard_as_spanish_is_other_language`, `test_a_passage_whisper_may_have_made_up_is_low_confidence`, `test_the_verdict`; also `test_transcriber.py`: `test_the_recognizers_verdict_marks_a_passage_whose_sound_passes`, `test_sessions.py`: `test_a_passage_not_heard_as_spanish_is_marked_and_left_out_of_what_is_sorted`, `DictationSpanishTests.cs`: `TC_UC9_10_a_passage_not_heard_as_spanish_comes_back_marked`, and `DictationSpanishBrowserTests.cs`: `TC_UC9_12_spanish_is_chosen_and_dictated_from_the_keyboard` |
| TC-UC9-11 | NFR-14 as reworded (step 17): Spanish dictation accuracy, and a Spanish live transcript's speed | NFR-14, FR-07 | Accuracy: `eval/tests/test_gate_es.py`: `test_a_spanish_baseline_holds_the_results_to_its_machine_and_scores`, and CI's "Check the committed eval results" step, which checks `eval/results/latest-es.json`. Speed: `test_sessions.py`: `test_a_spanish_chunk_is_answered_in_time_while_whisper_is_busy_and_the_next_one_catches_up`, `test_a_spanish_stop_answers_within_its_budget_and_the_result_follows`; the eval's live chunk times (`latest-es.json`, `metrics.live`) |
| TC-UC9-12 | NFR-12 (step 17): the language chosen and a Spanish dictation made from the keyboard, in real Chromium, in both themes; Spanish labelled experimental, in its choice's words and with a tag at the head of its hint | NFR-12, proposed FR-61 | `DictationSpanishBrowserTests.cs`: `TC_UC9_12_spanish_is_chosen_and_dictated_from_the_keyboard`, `The_language_choice_fits_every_width_in_both_themes` |
| TC-UC9-13 | Alternative flow (step 17): a dictation reaches its limit, 2 minutes in Spanish and 3 in English. While recording, the time left beside Stop counts down from the limit, in no live region, and the status line says "Dictation stops in a minute, at its 2-minute limit." (3-minute), then 30 seconds and 10 seconds left, and nothing between; at the limit the page sends no more audio, stops, and shows and says "The dictation stopped at its 2-minute limit." (3-minute), and the dictation goes on to its review. The time left fits 1,280 and 360 pixels in both themes | proposed FR-61, FR-07, NFR-12 | `DictationSpanishBrowserTests.cs`: `TC_UC9_13_the_time_left_counts_down_and_the_dictation_stops_at_its_limit` (Spanish and English), `The_time_left_fits_every_width_in_both_themes`; also `DictationSpanishTests.cs`: `A_dictation_in_spanish_is_refused_past_its_limit`, and `test_sessions.py`: `test_a_dictation_in_spanish_is_refused_past_two_minutes_and_one_in_english_isnt` |
| TC-UC9-14 | Invalid input (step 17): a dictation made with English chosen fewer than two thirds of whose words are English, Spanish dictated with English chosen (Spanish with clinical words in it among them), offers no field and no proposal; the transcript stays as plain text, with the reason `transcript_not_english` and the page's words pointing at the Spanish choice. English with a Spanish phrase in it, most short clinical English, and a recorded visit are checked as ever; a short English dictation more than a third of whose words no source holds (a common diagnosis ICD-10-CM words differently, "Dyslipidemia, atorvastatin.", exam words, a British term, a lone name) is refused, a known limit; and Spanish that reads as English drug names, digits or a shared clinical word is let through to the English checks, another | proposed FR-61, FR-12, FR-13 | `test_validation.py`: `test_a_dictation_mostly_not_english_words_offers_no_field_and_no_proposal`, `test_english_is_checked_however_much_else_it_holds_and_however_short`, `test_a_short_dictation_of_clinical_words_is_offered`, `test_spanish_with_clinical_words_in_it_is_refused`, `test_a_short_dictation_with_too_few_english_words_is_refused_too`, `test_a_limit_a_short_dictation_of_exam_words_no_list_holds_is_refused`, `test_a_limit_spanish_that_reads_as_english_names_digits_or_shared_words_isnt_refused`; `test_englishwords.py`; also `test_sessions.py`: `test_a_dictation_mostly_not_english_words_offers_nothing_and_says_why`, `test_speaker_rules.py`: `test_a_window_with_too_few_english_words_is_checked_as_ever`, `DictationPageTests.cs`: `A_dictation_mostly_not_english_comes_back_unsorted_with_words_pointing_at_spanish`, `AiReviewBrowserTests.cs`: `A_dictation_mostly_not_english_reaches_its_review_saying_to_choose_spanish`, and `eval/tests/test_run_es.py`: `test_a_spanish_recording_dictated_as_english_goes_the_english_way_and_is_scored_for_fr14_only` |

Unprefixed `test_*.py` files are in `src/Ehr.Dictation/sidecar/tests/`, and unprefixed `*Tests.cs` files in
`tests/Ehr.Web.Tests/`.

## Limits

- TC-UC9-04 checks the sidecar's side of resuming: a chunk sent again is ignored, a chunk that skips ahead is told which
  to send, and the transcript carries on. The page's own retry after a dropped request isn't tested in a browser.
- TC-UC9-06's accuracy is the committed eval run, made on a developer Mac (it says so), not on the clinic server. The
  gate has no accepted baseline yet, so "no lower than the baseline" has nothing to compare with until one is accepted.
- TC-UC9-11's accuracy is the Spanish eval run on audio, `eval/results/latest-es.json`, committed with run B of
  2026-11-24. No test runs Whisper itself: its decoding is tested with stand-ins for its processor and
  model, and is first run on recordings by that eval. Run B, on 36 synthetic recordings of 16 scripts (Apple M5, the
  developer Mac, not the clinic server): word error rate 0.1131 (0.1093 without accents), drug names 79 of 101 heard
  word for word (97 within two letters). Whisper's errors that change the meaning of a sentence shown to the clinician,
  by sentence (the owner's ruling of 2026-11-24): e01's "Toma losartán 50 mg una vez al día." heard as "Toma los Artan
  50 miligramos una vez al día" (clean) and "los Sartan" (HVAC noise, 20 dB), so the English names a drug nobody said;
  e12's "Niega mareo, palpitaciones, dolor torácico y síncope." heard with a full stop after "palpitaciones" (clean,
  and monitor noise at 20 dB), so the English, following the transcript shown, denies only dizziness and palpitations
  and states chest pain and syncope: **a case only the reviewing clinician can catch**, since the English is faithful to
  the transcript beside it; and e04's "habones en ambos brazos" (wheals) heard as "abones", a change no check or scorer
  compares. All are accepted by the owner as known limits (`eval/known-limitations.toml`).
- TC-UC9-14's rule, measured on the stored transcripts (2026-11-25), against its line of two thirds: none of the
  English eval's 100 recordings is refused; the nearest, d16 and d48 at babble noise (0 and 5 dB), have 12 of their
  15 words English (0.80, 0.13 above the line); then 0.907. None of the English text set's 51 scripts (lowest 0.937),
  none of the 590 trick sentences (lowest 0.75, 0.08 above) and, were it applied, none of the 22 visits (0.955). Run
  B's Spanish script dictated with English chosen (e01) is refused: 31 of its 65 words English (0.477), 0.19 under the
  line, the one such recording measured. Whisper's own transcripts of the 36 Spanish recordings, read the same way,
  have at most 0.463, and the 16 Spanish display texts at most 0.575 (e10, which mixes English in), all refused.
  Short English, each sentence of the 51 English scripts' gold fields taken alone as its own dictation: 2 of 712 are
  refused, "Bibasal crackles." (0.5) and "Salbutamol nebulizer here, then via spacer at home." (0.625), and the nearest
  above the line by length are 0.667 at 1 to 3 words ("EASI score 18."), 0.714 at 4 to 7, 0.818 at 8 to 15 and 0.929
  at 16 or more; the 51 display texts are all offered (lowest 0.965). A second measurement, the re-check's: of 117
  common diagnosis words a reviewer tried, 19 aren't counted, since ICD-10-CM's descriptions word them differently
  ("onychomycosis", "costochondritis", "nephrolithiasis", "dyslipidemia", "leukocytosis", "rhinorrhea", "preeclampsia",
  "presyncope" among them), so a one-line assessment of one ("Dyslipidemia, atorvastatin.", "Nephrolithiasis,
  tamsulosin.", each 0.5) is refused. Before the review's fix, 8 of the 712 were
  refused ("Uncomplicated cystitis.", "Strep pharyngitis.", "Severe alopecia areata." among them). Of 796 Spanish
  sentences taken alone (Whisper's and the display texts'), 27 are over the line, those of numbers and units ("Peso 96
  kg.") and the English ones of the scripts that mix English in, and none of them because of the clinical words. A
  known limit, as the English checks still ground every value in the transcript: Spanish that reads as English drug
  names, digits or a clinical word both languages spell alike reaches them ("Toma metformin, lisinopril, atorvastatin,
  aspirin, omeprazole, losartan, amlodipine y furosemide.", 0.9; "Dolor abdominal con urticaria y prurito.", 0.667).
  And a refused dictation still waits for the model's answer first: refusing before the model is asked would change
  what makes the answers, the pipeline the eval's results are tied to.
