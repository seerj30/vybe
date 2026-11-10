# syntax=docker/dockerfile:1
# VYBE art pipeline (docs/art/README.md): Blender 4.5 LTS as a Python module (the official `bpy` wheel), headless.
# linux/amd64 on purpose: the same image runs in CI (ubuntu-24.04) and on an Apple-silicon Mac (Docker Desktop
# runs it with Rosetta), so both build the same bytes. Build it with assets/source/build.sh, not by hand.
FROM --platform=linux/amd64 python:3.11-slim-bookworm@sha256:a83ba18aef0f865b1a4a611c2cf460fe446f2795673bbb9fe43be570439e3b39
# Shared libraries that the bpy module links against (X11 and OpenGL stubs; nothing opens a display).
RUN apt-get update \
 && apt-get install -y --no-install-recommends libx11-6 libxrender1 libxxf86vm1 libxfixes3 libxi6 libxkbcommon0 \
      libsm6 libgl1 libegl1 libgomp1 \
 && rm -rf /var/lib/apt/lists/*
COPY requirements.txt /tmp/requirements.txt
RUN pip install --no-cache-dir --require-hashes -r /tmp/requirements.txt \
 && python -c "import bpy; print(bpy.app.version_string)"
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONHASHSEED=0
WORKDIR /work
