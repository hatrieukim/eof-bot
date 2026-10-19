#!/usr/bin/env bash
# Sync the code to a Docker host over SSH, then rebuild and restart the container.
# Usage: DEPLOY_HOST=user@server DEPLOY_DIR=eof-bot ./deploy.sh
#   DEPLOY_HOST  SSH destination (host alias from ~/.ssh/config or user@host), required
#   DEPLOY_DIR   target directory on the host, relative to the remote home (default: eof-bot)
set -euo pipefail
cd "$(dirname "$0")"

: "${DEPLOY_HOST:?Set DEPLOY_HOST, e.g. DEPLOY_HOST=user@server ./deploy.sh}"
DEPLOY_DIR="${DEPLOY_DIR:-eof-bot}"

# --delete: files removed locally are removed on the host too. Excluded paths are left untouched there
# (the host's .env holds its own keys; data/ holds the live SQLite DB and documents).
rsync -av --delete \
  --exclude '.env' \
  --exclude 'data/' \
  --exclude 'bin/' --exclude 'obj/' \
  --exclude '.idea/' --exclude '.vs/' --exclude '.vscode/' --exclude '.claude/' --exclude '.DS_Store' \
  --exclude '.git/' \
  ./ "$DEPLOY_HOST:$DEPLOY_DIR/"

# Documents are not pushed: the copy on the host is the source of truth and admins edit it at /admin.
ssh "$DEPLOY_HOST" "cd $DEPLOY_DIR && docker compose up -d --build --force-recreate"
