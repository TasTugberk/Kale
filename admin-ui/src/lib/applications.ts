import { apiPath, change, jsonBody, nothing, read, type ApiCall, type Outcome } from "./admin-requests";

/** The admin API's applications (/admin/api/applications). */

export interface Application {
  id: string;
  key: string;
  name: string;
  isActive: boolean;
}

export interface NewApplication {
  key: string;
  name: string;
  redirectUris: string[];
  postLogoutRedirectUris: string[];
}

/** A new client secret. AuthService keeps only its hash, so this is the only time anyone sees it. */
export interface CreatedApplication {
  key: string;
  clientSecret: string;
}

export function listApplications(call: ApiCall): Promise<Application[]> {
  return read<Application[]>(call, "applications");
}

export function createApplication(call: ApiCall, application: NewApplication): Promise<Outcome<CreatedApplication>> {
  return change(call, "applications", jsonBody("POST", application), async response => {
    const created = (await response.json()) as CreatedApplication;
    return { key: created.key, clientSecret: created.clientSecret };
  });
}

/** Replaces the client secret; the old one stops working at once. */
export function rotateSecret(call: ApiCall, id: string): Promise<Outcome<string>> {
  return change(call, apiPath`applications/${id}/secret`, { method: "POST" },
    async response => ((await response.json()) as { clientSecret: string }).clientSecret);
}

export function setApplicationActive(call: ApiCall, id: string, isActive: boolean): Promise<Outcome<undefined>> {
  return change(call, apiPath`applications/${id}/active`, jsonBody("PUT", { isActive }), nothing);
}

/** The "new application" form. Only trimmed here: AuthService checks the values and says what's wrong. */
export function newApplicationFromForm(form: FormData): NewApplication {
  return {
    key: text(form, "key"),
    name: text(form, "name"),
    redirectUris: linesOf(text(form, "redirectUris")),
    postLogoutRedirectUris: linesOf(text(form, "postLogoutRedirectUris")),
  };
}

/** A text area's non-empty lines, trimmed. */
export function linesOf(value: string): string[] {
  return value.split("\n").map(line => line.trim()).filter(line => line !== "");
}

export function text(form: FormData, name: string): string {
  const value = form.get(name);
  return typeof value === "string" ? value.trim() : "";
}
