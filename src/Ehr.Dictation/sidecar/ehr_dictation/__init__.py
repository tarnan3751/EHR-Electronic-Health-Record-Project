"""The dictation sidecar (FR-10 to FR-14): dictated audio in, a transcript and checked note-field proposals out.

It runs on the on-prem server only, on a network with no route out, and keeps nothing: a session's audio and transcript
live in memory until the clinician stops, cancels or walks away. The modules, in the order audio flows through them:

- audio: decodes whatever container the browser recorded into 16 kHz mono samples, with ffmpeg, in memory.
- segmenter, asr, quality, transcript, transcriber: MedASR transcription, cut at pauses so partial transcripts stay
  cheap, with passages too quiet, too noisy or too uncertain to trust marked so they can be dictated again.
- prompt, llama: K2 Horizon, in llama-server, sorts the finished transcript into History, Assessment and Plan.
- validation: every proposed value is checked against the transcript; one that fails is never shown.
- provenance: which model files, prompt and schema produced a result.
- sessions, api, settings: the HTTP API the on-prem app calls (Ehr.Web, Api/Dictation).

The eval (eval/) imports the same modules, so it measures exactly what ships.
"""
