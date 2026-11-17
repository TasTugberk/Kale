# Shared by the dev scripts (source it, then call load_env from the repository root).
#
# Loads .env, creating it from .env.example on first run. Keys added to .env.example since then are appended
# with their defaults, so an older .env keeps working with the current compose.yaml.
load_env() {
  load_env_file .env .env.example
}

# The same for any settings file and its example (the production-like example in deploy/ uses its own pair).
load_env_file() {
  local file="$1" example="$2"
  if [[ ! -f "$file" ]]; then
    cp "$example" "$file"
    echo "Created $file from $example"
  fi

  # A hand-edited file may not end with a newline; appending would then join two keys on one line.
  if [[ -s "$file" && -n "$(tail -c 1 "$file")" ]]; then
    echo >> "$file"
  fi

  local line key
  while IFS= read -r line; do
    key="${line%%=*}"
    if [[ "$line" =~ ^[A-Z_]+= ]] && ! grep -q "^${key}=" "$file"; then
      echo "$line" >> "$file"
      echo "Added $key to $file"
    fi
  done < "$example"

  set -a
  source "$file"
  set +a
}
