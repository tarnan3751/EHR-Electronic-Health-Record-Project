"""The sidecar's settings, from the environment (compose.yaml sets them in the ai profile; see the root README to run it
natively). Secrets come from the environment only, never from a file in the repository.

    DICTATION_API_KEY     required: what the on-prem app sends (Ehr:Dictation:ApiKey); being on the network proves nothing
    LLAMA_API_KEY         required: llama-server's key
    DICTATION_LLAMA_URL   llama-server's address (default http://llama:8080, the ai profile's llama service)
    DICTATION_MODELS_DIR  where setup_models.py put the models (default /models, the ai profile's mount)
    DICTATION_DEVICE      where MedASR runs: cpu (default), mps on a Mac, cuda on an NVIDIA GPU

The limits are fixed here rather than read from the environment, so every server behaves the same:

    max_sessions, max_sessions_per_owner   8 and 2 sessions at once, counted until each one's result is ready
    max_chunk_bytes, max_session_bytes     2 MB a chunk, 16 MB a dictation
    max_session_seconds                    5 minutes of audio a dictation: what K2 Horizon can sort within its context
                                           (4,096 tokens a request) and its 90 seconds, measured
    idle_seconds                           a session no audio has reached for 10 minutes is discarded
    result_seconds                         a result is discarded 10 minutes after it is ready
    structuring_seconds                    K2 Horizon has 90 seconds, its wait for a turn included
    sorting_at_once                        how many dictations K2 Horizon sorts at once (llama-server's slots)
    call_seconds                           a chunk is answered within 15 seconds, transcript updated or not
    stop_wait_seconds                      stop answers within 8 seconds, with whatever is ready
    max_result_wait_seconds                result waits at most 10 seconds for the fields

The on-prem app gives every call 20 seconds (Ehr.Web, Api/Dictation), so no answer here may take longer.
"""

from __future__ import annotations

import os
from collections.abc import Mapping
from dataclasses import dataclass
from pathlib import Path


class SettingsError(Exception):
    """A setting the sidecar can't start without."""


@dataclass(frozen=True)
class Settings:
    api_key: str
    llama_api_key: str
    llama_url: str = "http://llama:8080"
    models_dir: Path = Path("/models")
    device: str = "cpu"

    max_sessions: int = 8
    max_sessions_per_owner: int = 2
    max_chunk_bytes: int = 2 * 1024 * 1024
    max_session_bytes: int = 16 * 1024 * 1024
    max_session_seconds: int = 300
    idle_seconds: float = 600.0  # a session no audio has reached for this long is discarded
    result_seconds: float = 600.0  # a finished result is discarded this long after it was ready
    structuring_seconds: float = 90.0  # K2 Horizon's time to answer, after which the fields are left unsorted
    sorting_at_once: int = 2
    call_seconds: float = 15.0
    stop_wait_seconds: float = 8.0  # how long stop waits for the transcript and fields before answering without them
    max_result_wait_seconds: float = 10.0

    @classmethod
    def from_environment(cls, environ: Mapping[str, str] = os.environ) -> Settings:
        api_key = environ.get("DICTATION_API_KEY", "").strip()
        if not api_key:
            raise SettingsError("Set DICTATION_API_KEY (in the ai profile: DICTATION_API_KEY in infra/compose/.env).")
        llama_api_key = environ.get("LLAMA_API_KEY", "").strip()
        if not llama_api_key:
            raise SettingsError("Set LLAMA_API_KEY to llama-server's API key.")
        return cls(
            api_key=api_key,
            llama_api_key=llama_api_key,
            llama_url=environ.get("DICTATION_LLAMA_URL", cls.llama_url),
            models_dir=Path(environ.get("DICTATION_MODELS_DIR", str(cls.models_dir))),
            device=environ.get("DICTATION_DEVICE", cls.device),
        )
