"""The dictation eval: the test set's scripts and gold note fields (scripts), how transcripts are compared (normalize,
dates), the measures (scoring), a full run against the models (run, feeding them through answers), and the check CI
makes on the committed results (gate). The visits for listening to a visit have their own (conversations, listening,
ambient, ambient_scoring, ambient_gate), which the gate runs too. It imports the sidecar (ehr_dictation) rather than
copying any of it, so it measures exactly what ships.
"""
