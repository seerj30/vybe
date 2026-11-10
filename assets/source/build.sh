#!/usr/bin/env bash
# Builds the VYBE avatar and catalog art (docs/art/README.md) in the pinned Blender image and publishes
# assets/published/3d and the manifests. Same command on an Apple-silicon Mac (Docker Desktop) and in CI.
#   assets/source/build.sh               build and publish
#   assets/source/build.sh --previews    also render outfit previews to assets/build/previews (not published)
set -euo pipefail
cd "$(dirname "$0")/../.."

# The image tag follows the Dockerfile and the pinned requirements, so a change rebuilds it.
tag=$(cat assets/source/Dockerfile assets/source/requirements.txt | shasum -a 256 | cut -c1-12)
image="vybe-art:${tag}"
if ! docker image inspect "$image" >/dev/null 2>&1; then
  docker build --platform linux/amd64 -t "$image" -f assets/source/Dockerfile assets/source
fi

rm -rf assets/build
docker run --rm --platform linux/amd64 --network none \
  -u "$(id -u):$(id -g)" -e HOME=/tmp \
  -v "$PWD:/work" -w /work "$image" \
  python assets/source/build.py "$@"
