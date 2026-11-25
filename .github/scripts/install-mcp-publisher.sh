#!/usr/bin/env bash
# Installs the MCP Registry's publisher CLI at a pinned release into the current folder, verified with cosign against
# the release workflow that built it (needs sigstore/cosign-installer first). Usage: install-mcp-publisher.sh <tag>
set -euo pipefail
version="$1"
os="$(uname -s | tr '[:upper:]' '[:lower:]')"
arch="$(uname -m | sed 's/x86_64/amd64/;s/aarch64/arm64/')"
archive="mcp-publisher_${os}_${arch}.tar.gz"
base_url="https://github.com/modelcontextprotocol/registry/releases/download/${version}"

curl --fail --location --silent --show-error --output "$archive" "$base_url/$archive"
curl --fail --location --silent --show-error --output "$archive.sigstore.json" "$base_url/$archive.sigstore.json"
cosign verify-blob \
  --bundle "$archive.sigstore.json" \
  --certificate-identity "https://github.com/modelcontextprotocol/registry/.github/workflows/release.yml@refs/tags/${version}" \
  --certificate-oidc-issuer "https://token.actions.githubusercontent.com" \
  "$archive"
tar xzf "$archive" mcp-publisher
rm -f "$archive" "$archive.sigstore.json"
./mcp-publisher --version || true
