/**
 * Reading the admin API's answers, shared by the pages. Free of Next.js: the pages pass in adminApi from
 * server.ts, the tests a fake.
 */

/** Sends a request to /admin/api/{path} as the signed-in admin. */
export type ApiCall = (path: string, init?: RequestInit) => Promise<Response>;

/**
 * One reason AuthService gave for a refusal: a stable code (shown in the admin's language from the "Refusals"
 * texts), the values that text needs (e.g. a name), and AuthService's English text for a code we have no text for.
 */
export interface RefusalReason {
  code: string;
  values: Record<string, string>;
  detail: string;
}

/**
 * What a change hands back to its form: done, refused with AuthService's reasons (a 400; `detail` is all of them
 * in English), or the item is gone (a 404, e.g. deleted in another tab).
 */
export type Outcome<T> =
  | { ok: true; value: T }
  | { ok: false; refusal: "invalid"; detail: string | undefined; reasons?: RefusalReason[] }
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

/** Every value sent under one name (e.g. a multi-select's choices), trimmed, without blanks or duplicates. */
export function listFromForm(form: FormData, name: string): string[] {
  const values = form.getAll(name).filter(value => typeof value === "string").map(value => value.trim());
  return [...new Set(values.filter(value => value !== ""))];
}

/**
 * Several changes of one kind (e.g. the roles chosen in a multi-select), one after another, because the admin API
 * takes one item per request. Stops at the first refusal and returns it; the changes before it stay made.
 */
export async function changeEach(
  ids: string[], changeOne: (id: string) => Promise<Outcome<undefined>>,
): Promise<Outcome<undefined>> {
  for (const id of ids) {
    const outcome = await changeOne(id);
    if (!outcome.ok) {
      return outcome;
    }
  }

  return { ok: true, value: undefined };
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
    return { ok: false, refusal: "invalid", ...await problem(response) };
  }

  if (response.status === 404) {
    return { ok: false, refusal: "not-found" };
  }

  throw new Error(`Admin API ${init.method} ${path} answered ${response.status}.`);
}

type Translate = (key: string, values?: Record<string, string>) => string;

/**
 * The text a form shows for a refusal (none before the first submit or after a success). `t` is next-intl's
 * translator for the "Forms" texts, `refusals` the one for the "Refusals" texts, keyed by AuthService's codes.
 * A code without a text (e.g. one newer than this UI) shows AuthService's English instead.
 */
export function refusalMessage(
  state: FormState<unknown>, t: Translate, refusals: Translate & { has: (key: string) => boolean },
): string | undefined {
  if (!state || state.ok) {
    return undefined;
  }

  if (state.refusal === "not-found") {
    return t("notFound");
  }

  if (state.reasons && state.reasons.length > 0) {
    const texts = state.reasons.map(reason => refusals.has(reason.code) ? refusals(reason.code, reason.values) : reason.detail);
    return t("refused", { detail: texts.join(" ") });
  }

  return state.detail ? t("refused", { detail: state.detail }) : t("invalid");
}

/** For changes that answer with no body. */
export async function nothing(): Promise<undefined> {
  return undefined;
}

// A 400 from the services is a problem response with every reason in English in "detail", and each one with its
// code in "reasons"; one from a body that didn't bind may have no body at all.
async function problem(response: Response): Promise<{ detail: string | undefined; reasons?: RefusalReason[] }> {
  let body: { detail?: unknown; reasons?: unknown };
  try {
    body = (await response.json()) as typeof body;
  } catch {
    return { detail: undefined };
  }

  const detail = typeof body.detail === "string" ? body.detail : undefined;
  if (!Array.isArray(body.reasons)) {
    return { detail };
  }

  return { detail, reasons: body.reasons.flatMap(reasonFrom) };
}

// One reason, or none when it isn't shaped like AuthService's. Values are shown as text, so only strings count.
function reasonFrom(item: unknown): RefusalReason[] {
  if (typeof item !== "object" || item === null) {
    return [];
  }

  const { code, detail, values } = item as { code?: unknown; detail?: unknown; values?: unknown };
  if (typeof code !== "string" || typeof detail !== "string") {
    return [];
  }

  const stringValues: Record<string, string> = {};
  if (typeof values === "object" && values !== null) {
    for (const [name, value] of Object.entries(values)) {
      if (typeof value === "string") {
        stringValues[name] = value;
      }
    }
  }

  return [{ code, values: stringValues, detail }];
}
