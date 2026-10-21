import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { readConfig } from "../src/lib/config";

// The settings compose gives the admin UI; the client secret is a mounted Podman secret (a file).
function settings(overrides: Record<string, string | undefined> = {}): NodeJS.ProcessEnv {
  const secretFile = join(mkdtempSync(join(tmpdir(), "kale-admin-ui-")), "secret");
  writeFileSync(secretFile, "the-secret\n");
  return {
    NODE_ENV: "test",
    KALE_ENVIRONMENT: "Production",
    KALE_AUTH_ISSUER: "https://auth.example/",
    KALE_ADMIN_UI_URL: "https://admin.example/",
    KALE_ADMIN_CLIENT_SECRET_FILE: secretFile,
    ...overrides,
  };
}

describe("readConfig", () => {
  it("reads the settings, with the client secret from its file", () => {
    const config = readConfig(settings());

    expect(config.issuer.href).toBe("https://auth.example/");
    expect(config.publicUrl.href).toBe("https://admin.example/");
    expect(config.clientId).toBe("auth");
    expect(config.clientSecret).toBe("the-secret");
    expect(config.allowInsecureHttp).toBe(false);
  });

  it("refuses a missing setting, naming it", () => {
    expect(() => readConfig(settings({ KALE_AUTH_ISSUER: undefined }))).toThrow("KALE_AUTH_ISSUER");
  });

  it("refuses plain http without the dev-only flag", () => {
    expect(() => readConfig(settings({ KALE_AUTH_ISSUER: "http://auth.example/" }))).toThrow("https");
  });

  it("refuses the dev-only flag outside Development and Testing", () => {
    expect(() => readConfig(settings({ KALE_ALLOW_INSECURE_HTTP_DEV_ONLY: "true" }))).toThrow("KALE_ALLOW_INSECURE_HTTP_DEV_ONLY");
  });

  it("allows plain http with the flag in Development", () => {
    const config = readConfig(settings({
      KALE_ENVIRONMENT: "Development",
      KALE_ALLOW_INSECURE_HTTP_DEV_ONLY: "true",
      KALE_AUTH_ISSUER: "http://auth.kale.localhost:8080/",
      KALE_ADMIN_UI_URL: "http://localhost:8083/",
    }));

    expect(config.allowInsecureHttp).toBe(true);
  });
});
