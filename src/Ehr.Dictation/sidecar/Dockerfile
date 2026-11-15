# The dictation sidecar (ehr_dictation), for the ai profile of the local server stack and the on-prem server. Built
# from src/Ehr.Dictation, so the image holds the package and the note schema and prompt it reads, at the same relative
# paths as in the repository. No model is in the image: setup_models.py's folder is mounted at /models. CI builds it
# for linux/amd64 and linux/arm64; it's never pushed from a laptop.
FROM ghcr.io/astral-sh/uv:0.13.1 AS uv

FROM python:3.12-slim-trixie

# ffmpeg decodes whatever container the browser recorded.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

COPY --from=uv /uv /usr/local/bin/uv
ENV UV_COMPILE_BYTECODE=1 UV_LINK_MODE=copy UV_PROJECT_ENVIRONMENT=/app/.venv UV_PYTHON_DOWNLOADS=never

# The packages exactly as uv.lock pins them, before the code, so a code change doesn't reinstall them.
WORKDIR /app/sidecar
COPY sidecar/pyproject.toml sidecar/uv.lock ./
RUN uv sync --locked --no-dev --no-install-project
COPY schema /app/schema
COPY sidecar/ehr_dictation ./ehr_dictation
RUN uv sync --locked --no-dev

# Nothing is fetched at run time: the models load from /models only. HOME is /tmp, a tmpfs in the stack, for anything
# a library wants to cache; the rest of the file system is read-only there.
ENV PATH=/app/.venv/bin:$PATH \
    HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 HF_HUB_DISABLE_TELEMETRY=1 \
    HOME=/tmp \
    DICTATION_MODELS_DIR=/models
USER 10001:10001
EXPOSE 8000
CMD ["uvicorn", "--factory", "ehr_dictation.api:create_app", "--host", "0.0.0.0", "--port", "8000", "--no-server-header"]
