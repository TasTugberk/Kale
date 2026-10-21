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

/** Builds a path from fixed parts and ids; ids are escaped, so one can't point at another path. */
export function apiPath(strings: TemplateStringsArray, ...ids: string[]): string {
  let path = strings[0];
  for (let i = 0; i < ids.length; i++) {
    path += encodeURIComponent(ids[i]) + strings[i + 1];
  }

  return path;
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
