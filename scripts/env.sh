# Shared by the dev scripts (source it, then call load_env from the repository root).
#
# Loads .env, creating it from .env.example on first run. Keys added to .env.example since then are appended
# with their defaults, so an older .env keeps working with the current compose.yaml.
load_env() {
  if [[ ! -f .env ]]; then
    cp .env.example .env
    echo "Created .env from .env.example"
  fi

  # A hand-edited .env may not end with a newline; appending would then join two keys on one line.
  if [[ -s .env && -n "$(tail -c 1 .env)" ]]; then
    echo >> .env
  fi

  local line key
  while IFS= read -r line; do
    key="${line%%=*}"
    if [[ "$line" =~ ^[A-Z_]+= ]] && ! grep -q "^${key}=" .env; then
      echo "$line" >> .env
      echo "Added $key to .env"
    fi
  done < .env.example

  set -a
  source .env
  set +a
}
