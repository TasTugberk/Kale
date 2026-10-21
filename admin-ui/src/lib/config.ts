import { readFileSync } from "node:fs";

/** The admin UI's settings. Compose sets them; the client secret comes from a mounted Podman secret (a file). */
export interface AdminUiConfig {
  /** AuthService's public address (its issuer); the admin API is under it, at admin/api/. */
  issuer: URL;
  /** Where browsers reach this UI; the sign-in and sign-out return addresses are built from it. */
  publicUrl: URL;
  /** The admin UI signs in to AuthService's own application, "auth". */
  clientId: string;
  clientSecret: string;
  /** Plain http to AuthService and for this UI's cookies: local development only. */
  allowInsecureHttp: boolean;
}

export function readConfig(env: NodeJS.ProcessEnv): AdminUiConfig {
  const isDevOrTest = env.KALE_ENVIRONMENT === "Development" || env.KALE_ENVIRONMENT === "Testing";
  const allowInsecureHttp = env.KALE_ALLOW_INSECURE_HTTP_DEV_ONLY === "true";
  if (allowInsecureHttp && !isDevOrTest) {
    throw new Error("KALE_ALLOW_INSECURE_HTTP_DEV_ONLY is only allowed when KALE_ENVIRONMENT is Development or Testing.");
  }

  const issuer = absoluteUrl(env, "KALE_AUTH_ISSUER", allowInsecureHttp);
  const publicUrl = absoluteUrl(env, "KALE_ADMIN_UI_URL", allowInsecureHttp);

  // A file in a container (never an environment variable there); the variable is for running it locally.
  const secretFile = env.KALE_ADMIN_CLIENT_SECRET_FILE;
  const clientSecret = secretFile ? readFileSync(secretFile, "utf8").trim() : required(env, "KALE_ADMIN_CLIENT_SECRET");

  return { issuer, publicUrl, clientId: env.KALE_ADMIN_CLIENT_ID ?? "auth", clientSecret, allowInsecureHttp };
}

function required(env: NodeJS.ProcessEnv, name: string): string {
  const value = env[name];
  if (!value) {
    throw new Error(`Set ${name}.`);
  }

  return value;
}

// Absolute, ending with "/" (relative paths are added to it), and https unless the dev-only flag allows http.
function absoluteUrl(env: NodeJS.ProcessEnv, name: string, allowInsecureHttp: boolean): URL {
  const value = required(env, name);
  const url = new URL(value.endsWith("/") ? value : value + "/");
  if (url.protocol !== "https:" && !(url.protocol === "http:" && allowInsecureHttp)) {
    throw new Error(`${name} must be an https address (plain http only with KALE_ALLOW_INSECURE_HTTP_DEV_ONLY, for local development).`);
  }

  return url;
}
