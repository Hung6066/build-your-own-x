# Multi-stage build: CUDA runtime + Python deps + service
FROM nvidia/cuda:12.4.1-cudnn-runtime-ubuntu22.04 AS base

ENV DEBIAN_FRONTEND=noninteractive \
    PIP_NO_CACHE_DIR=1 \
    PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1

RUN apt-get update && apt-get install -y --no-install-recommends \
    python3.11 python3.11-venv python3-pip git ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*

RUN ln -sf /usr/bin/python3.11 /usr/local/bin/python && \
    ln -sf /usr/bin/python3.11 /usr/local/bin/python3

WORKDIR /app

COPY requirements.txt /app/requirements.txt
RUN python -m pip install --upgrade pip && \
    python -m pip install -r requirements.txt

COPY . /app

RUN useradd -ms /bin/bash hope && \
    mkdir -p /var/lib/hope-finetune && chown -R hope:hope /var/lib/hope-finetune /app
USER hope

EXPOSE 8765

HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8765/healthz || exit 1

CMD ["python", "-m", "uvicorn", "app.api.app:create_app", \
    "--factory", "--host", "0.0.0.0", "--port", "8765", "--log-config", "/dev/null"]
