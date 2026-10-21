/**
 * Reading the admin API's answers, shared by the pages. Free of Next.js: the pages pass in adminApi from
 * server.ts, the tests a fake.
 */

/** Sends a request to /admin/api/{path} as the signed-in admin. */
export type ApiCall = (path: string, init?: RequestInit) => Promise<Response>;

/**
 * What a change hands back to its form: done, refused with AuthService's reason (a 400; the reason is in
 * English, from AuthService), or the item is gone (a 404, e.g. deleted in another tab).
 */
export type Outcome<T> =
  | { ok: true; value: T }
  | { ok: false; refusal: "invalid"; detail: string | undefined }
  | { ok: false; refusal: "not-found" };

/** A form's state before its first submit. */
export type FormState<T> = Outcome<T> | undefined;

/**
 * A form's state that also keeps what was sent when it was refused. React empties a form after its action runs;
 * with this, the fields start again from what the admin typed, so they can correct it.
 */
export interface SubmitState<T, S> {
  outcome: Outcome<T>;
  submitted: S | undefined;
}

export function afterSubmit<T, S>(outcome: Outcome<T>, submitted: S): SubmitState<T, S> {
  return { outcome, submitted: outcome.ok ? undefined : submitted };
}

/** A form field's text, trimmed; empty when it's missing. */
export function text(form: FormData, name: string): string {
  const value = form.get(name);
  return typeof value === "string" ? value.trim() : "";
}

export function jsonBody(method: string, body: unknown): RequestInit {
  return { method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) };
}

/** Reads a list or an item; anything but success is an error for the page's error screen. */
export async function read<T>(call: ApiCall, path: string): Promise<T> {
  const response = await call(path, undefined);
  if (!response.ok) {
    throw new Error(`Admin API GET ${path} answered ${response.status}.`);
  }

  return (await response.json()) as T;
}

/** Reads one item by its id; undefined when there's no such item (the page then shows "not found"). */
export async function find<T>(call: ApiCall, path: string): Promise<T | undefined> {
  const response = await call(path, undefined);
  if (response.status === 404) {
    return undefined;
  }

  if (!response.ok) {
    throw new Error(`Admin API GET ${path} answered ${response.status}.`);
  }

  return (await response.json()) as T;
}

/**
 * Sends a change. A refusal (400) or a missing item (404) goes back to the form; any other failure is an error
 * for the page's error screen.
 */
export async function change<T>(
  call: ApiCall, path: string, init: RequestInit, readValue: (response: Response) => Promise<T>,
): Promise<Outcome<T>> {
  const response = await call(path, init);
  if (response.ok) {
    return { ok: true, value: await readValue(response) };
  }

  if (response.status === 400) {
    return { ok: false, refusal: "invalid", detail: await problemDetail(response) };
  }

  if (response.status === 404) {
    return { ok: false, refusal: "not-found" };
  }

  throw new Error(`Admin API ${init.method} ${path} answered ${response.status}.`);
}

/**
 * The text a form shows for a refusal (none before the first submit or after a success). `t` is next-intl's
 * translator for the "Forms" texts.
 */
export function refusalMessage(
  state: FormState<unknown>, t: (key: string, values?: Record<string, string>) => string,
): string | undefined {
  if (!state || state.ok) {
    return undefined;
  }

  if (state.refusal === "not-found") {
    return t("notFound");
  }

  return state.detail ? t("refused", { detail: state.detail }) : t("invalid");
}

/** For changes that answer with no body. */
export async function nothing(): Promise<undefined> {
  return undefined;
}

// A 400 from the services is a problem response with the reason in "detail"; one from a body that didn't bind
// may have no body at all.
async function problemDetail(response: Response): Promise<string | undefined> {
  try {
    const body = (await response.json()) as { detail?: unknown };
    return typeof body.detail === "string" ? body.detail : undefined;
  } catch {
    return undefined;
  }
}
