#!/usr/bin/env bash
# bootstrap-macos.sh — Prepare a macOS machine to run CloudHealthOffice locally
# on Docker Desktop Kubernetes.
#
# Usage:
#   ./scripts/bootstrap-macos.sh           # check, then install what is missing
#   ./scripts/bootstrap-macos.sh --check   # report only, install nothing
#
# Safe to re-run: every step checks before it installs.
#
# IMPORTANT — this script is deliberately written for bash 3.2, the version
# macOS ships. One of its jobs is installing a newer bash (deploy-local.sh needs
# 4+ for associative arrays), so it cannot itself depend on bash 4 syntax. Keep
# it free of: associative arrays, ${var,,}, mapfile/readarray, and `local -n`.

set -euo pipefail

CHECK_ONLY=false
for arg in "$@"; do
  case "$arg" in
    --check) CHECK_ONLY=true ;;
    -h|--help) sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown argument: $arg (try --help)" >&2; exit 1 ;;
  esac
done

# ── output helpers ───────────────────────────────────────────────────────────
if [ -t 1 ]; then
  C_OK=$'\033[32m'; C_WARN=$'\033[33m'; C_ERR=$'\033[31m'; C_DIM=$'\033[2m'; C_OFF=$'\033[0m'
else
  C_OK=""; C_WARN=""; C_ERR=""; C_DIM=""; C_OFF=""
fi
ok()    { echo "  ${C_OK}✓${C_OFF} $1"; }
warn()  { echo "  ${C_WARN}!${C_OFF} $1"; }
bad()   { echo "  ${C_ERR}✗${C_OFF} $1"; }
info()  { echo "  ${C_DIM}·${C_OFF} $1"; }
head_() { echo ""; echo "── $1"; }

MISSING=0
MANUAL=0

# `--check` turns every install into a report line instead.
would_install() {
  if [ "$CHECK_ONLY" = true ]; then
    bad "$1 — missing (run without --check to install)"
    MISSING=$((MISSING + 1))
    return 1
  fi
  return 0
}

# ── platform ─────────────────────────────────────────────────────────────────
head_ "Platform"
if [ "$(uname -s)" != "Darwin" ]; then
  bad "this script targets macOS; found $(uname -s)."
  info "on Linux, install Docker + a local Kubernetes yourself, then run scripts/deploy-local.sh"
  exit 1
fi
ARCH="$(uname -m)"
ok "macOS $(sw_vers -productVersion 2>/dev/null || echo '?') on ${ARCH}"
if [ "$ARCH" = "arm64" ]; then
  info "Apple Silicon — every image this stack uses publishes arm64, so nothing runs under emulation"
fi

# ── Homebrew ─────────────────────────────────────────────────────────────────
head_ "Homebrew"
if command -v brew >/dev/null 2>&1; then
  ok "brew $(brew --version 2>/dev/null | head -1 | awk '{print $2}') at $(brew --prefix)"
else
  if would_install "Homebrew"; then
    warn "installing Homebrew (it will prompt for your password)"
    /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
    # Apple Silicon installs to /opt/homebrew, Intel to /usr/local; pick whichever exists.
    if [ -x /opt/homebrew/bin/brew ]; then
      eval "$(/opt/homebrew/bin/brew shellenv)"
    elif [ -x /usr/local/bin/brew ]; then
      eval "$(/usr/local/bin/brew shellenv)"
    fi
    ok "Homebrew installed"
    warn "add it to future shells if the installer did not:"
    info 'eval "$(/opt/homebrew/bin/brew shellenv)" >> ~/.zprofile'
  fi
fi

# ── bash 4+ ──────────────────────────────────────────────────────────────────
# macOS ships bash 3.2 (the last GPLv2 release). deploy-local.sh needs 4+.
head_ "bash 4+ (required by scripts/deploy-local.sh)"
BREW_BASH=""
if command -v brew >/dev/null 2>&1; then
  BREW_BASH="$(brew --prefix 2>/dev/null)/bin/bash"
fi
if [ -n "$BREW_BASH" ] && [ -x "$BREW_BASH" ]; then
  ok "$("$BREW_BASH" --version | head -1) at $BREW_BASH"
else
  info "system bash is ${BASH_VERSION:-unknown} (macOS ships 3.2 — too old)"
  if would_install "bash 4+"; then
    brew install bash
    ok "bash installed at $(brew --prefix)/bin/bash"
  fi
fi

# ── Docker Desktop ───────────────────────────────────────────────────────────
head_ "Docker Desktop"
if [ -d "/Applications/Docker.app" ]; then
  ok "Docker Desktop installed"
else
  if would_install "Docker Desktop"; then
    brew install --cask docker
    ok "Docker Desktop installed"
    MANUAL=$((MANUAL + 1))
    warn "launch Docker Desktop once to finish setup before continuing"
  fi
fi

if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
  ok "Docker daemon running ($(docker info --format '{{.ServerVersion}}' 2>/dev/null))"
else
  warn "Docker daemon not responding — open Docker Desktop and wait for the whale to settle"
  MANUAL=$((MANUAL + 1))
fi

# ── kubectl ──────────────────────────────────────────────────────────────────
head_ "kubectl"
if command -v kubectl >/dev/null 2>&1; then
  ok "kubectl $(kubectl version --client -o json 2>/dev/null | /usr/bin/python3 -c 'import json,sys; print(json.load(sys.stdin)["clientVersion"]["gitVersion"])' 2>/dev/null || echo 'present')"
else
  # Docker Desktop bundles kubectl, but only once Kubernetes has been enabled.
  if would_install "kubectl"; then
    brew install kubectl
    ok "kubectl installed"
  fi
fi

# ── Kubernetes in Docker Desktop ─────────────────────────────────────────────
# This is a GUI toggle; it cannot be flipped reliably from a script across
# Docker Desktop versions, so verify and instruct rather than guess.
head_ "Kubernetes (Docker Desktop)"
if command -v kubectl >/dev/null 2>&1 && kubectl cluster-info >/dev/null 2>&1; then
  CTX="$(kubectl config current-context 2>/dev/null || echo '?')"
  ok "cluster reachable (context: ${CTX})"
  if [ "$CTX" != "docker-desktop" ]; then
    warn "context is '${CTX}', not 'docker-desktop'"
    info "switch with: kubectl config use-context docker-desktop"
    MANUAL=$((MANUAL + 1))
  fi
else
  bad "no reachable cluster"
  info "enable it: Docker Desktop → Settings → Kubernetes → Enable Kubernetes → Apply & Restart"
  info "then re-run this script to verify"
  MANUAL=$((MANUAL + 1))
fi

# ── optional tooling ─────────────────────────────────────────────────────────
head_ "Optional"
if command -v jq >/dev/null 2>&1; then
  ok "jq $(jq --version 2>/dev/null)"
else
  if [ "$CHECK_ONLY" = true ]; then
    info "jq — not installed (optional; nicer script output)"
  else
    brew install jq && ok "jq installed"
  fi
fi

# The .NET SDK is NOT required to run the stack: every service is compiled
# inside its Docker image. It is only useful for running `dotnet test` directly.
if command -v dotnet >/dev/null 2>&1; then
  ok ".NET SDK $(dotnet --version 2>/dev/null) (not required — images build in Docker)"
else
  info ".NET SDK — not installed. Not needed to run the stack; images compile in Docker."
  info "only if you want to run tests directly: brew install --cask dotnet-sdk"
fi

# ── verdict ──────────────────────────────────────────────────────────────────
head_ "Result"
if [ "$CHECK_ONLY" = true ] && [ "$MISSING" -gt 0 ]; then
  bad "$MISSING tool(s) missing — re-run without --check to install"
  exit 1
fi
if [ "$MANUAL" -gt 0 ]; then
  warn "$MANUAL item(s) need you: see the messages above, then re-run --check"
  exit 1
fi

ok "environment ready"
echo ""
echo "Next:"
echo "  ./scripts/deploy-local.sh          # build images + deploy to local Kubernetes"
echo "  ./scripts/deploy-local.sh --help   # other modes"
echo ""
echo "First run pulls the .NET base images and builds ~25 services — budget ~8 GB"
echo "of disk and a long first build. Later runs reuse the Docker layer cache."
