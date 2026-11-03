import { randomBytes } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";

/**
 * The admin UI in a real browser, with JavaScript: what the plain form posts of smoke-test.sh can't show (dialogs,
 * forms after an action, confirmations). Creates applications, roles and users per run, like the smoke test.
 */

const admin = {
  userName: process.env.KALE_E2E_ADMIN_USER ?? "admin",
  password: process.env.KALE_E2E_ADMIN_PASSWORD ?? "",
};

// A client application that has registered operations and has roles: the compose stack's SampleApp by default.
const clientApp = process.env.KALE_E2E_CLIENT_APP ?? "sample";

// Each test makes its own names: Playwright loads this file again in a new worker after a failure.
function uniqueName(prefix: string) {
  return `${prefix}-${randomBytes(3).toString("hex")}`;
}

// Through AuthService's real sign-in page, as the first admin.
async function signIn(page: Page) {
  await page.goto("/login");
  await page.locator('input[name="UserName"]').fill(admin.userName);
  await page.locator('input[name="Password"]').fill(admin.password);
  await page.locator('button[type="submit"]').click();
  await expect(page.getByText(`Signed in as`)).toBeVisible();
}

async function createApplication(page: Page, key: string) {
  await page.goto("/applications");
  await page.getByRole("button", { name: "New application" }).click();
  const dialog = page.getByRole("dialog");
  await dialog.locator('input[name="key"]').fill(key);
  await dialog.locator('input[name="name"]').fill("Browser test");
  await dialog.locator('textarea[name="redirectUris"]').fill("https://e2e.example/signin-oidc");
  await dialog.getByRole("button", { name: "Create" }).click();
  await expect(dialog.getByRole("status").filter({ hasText: `Client secret of ${key}` })).toBeVisible();
  return dialog;
}

// The grid row whose <code> is exactly this key.
function rowOf(page: Page, key: string) {
  return page.locator("tr", { has: page.locator("code", { hasText: new RegExp(`^${key}$`) }) });
}

// The same row, searched for first: grids show 10 rows a page, and every run adds applications.
async function findRow(page: Page, key: string) {
  await page.getByRole("searchbox", { name: "Search" }).fill(key);
  return rowOf(page, key);
}

// Answers the confirmation dialog a ConfirmButton opens.
async function answer(page: Page, choice: "Confirm" | "Cancel") {
  await page.getByRole("alertdialog").getByRole("button", { name: choice }).click();
}

async function createUser(page: Page, displayName: string) {
  await page.goto("/users");
  const userName = uniqueName("e2e");
  await page.getByRole("button", { name: "New user" }).click();
  const dialog = page.getByRole("dialog");
  await dialog.locator('input[name="userName"]').fill(userName);
  await dialog.locator('input[name="displayName"]').fill(displayName);
  await dialog.locator('input[name="password"]').fill(`E2e-${randomBytes(8).toString("hex")}`);
  await dialog.getByRole("button", { name: "Create" }).click();
  // Creating a user opens their page.
  await expect(page.getByRole("heading", { name: displayName })).toBeVisible();
  return userName;
}

test.beforeAll(() => {
  if (!process.env.KALE_E2E_UI_URL || !admin.password) {
    throw new Error("Set KALE_E2E_UI_URL and KALE_E2E_ADMIN_PASSWORD (scripts/browser-test.sh does).");
  }
});

test("a new application's secret shows once, and a refused form keeps what was typed", async ({ page }) => {
  await signIn(page);
  const key = uniqueName("e2e");
  const dialog = await createApplication(page, key);

  await expect(dialog.locator("[data-secret]")).not.toBeEmpty();
  await dialog.getByRole("button", { name: "Done" }).click();
  await expect(await findRow(page, key)).toBeVisible();

  // The same key again: refused, and the fields still hold what was typed (React empties a form after its action).
  await page.getByRole("button", { name: "New application" }).click();
  await dialog.locator('input[name="key"]').fill(key);
  await dialog.locator('input[name="name"]').fill("Typed again");
  await dialog.getByRole("button", { name: "Create" }).click();

  await expect(dialog.getByRole("alert").filter({ hasText: "already exists" })).toBeVisible();
  await expect(dialog.locator('input[name="key"]')).toHaveValue(key);
  await expect(dialog.locator('input[name="name"]')).toHaveValue("Typed again");

  // Shown once: after a reload it's gone.
  await page.reload();
  await expect(page.locator("[data-secret]")).toHaveCount(0);
});

// A field's hint is read after it, not as part of its name (screen readers, and getByLabel in these tests).
test("a field is named by its label alone", async ({ page }) => {
  await signIn(page);
  await page.goto("/applications");
  await page.getByRole("button", { name: "New application" }).click();

  await expect(page.getByRole("dialog").getByRole("textbox", { name: "Key", exact: true })).toBeVisible();
});

test("the applications grid searches and sorts", async ({ page }) => {
  await signIn(page);
  const key = uniqueName("e2e");
  await (await createApplication(page, key)).getByRole("button", { name: "Done" }).click();

  await page.getByRole("searchbox", { name: "Search" }).fill(key);
  await expect(page.locator("tbody tr")).toHaveCount(1);
  await expect(rowOf(page, key)).toBeVisible();

  await page.getByRole("searchbox", { name: "Search" }).fill("no-such-application");
  await expect(page.getByText("Nothing matches the search.")).toBeVisible();

  await page.getByRole("searchbox", { name: "Search" }).fill("");
  await page.getByRole("button", { name: "Key" }).click();
  await expect(page.getByRole("columnheader", { name: "Key" })).toHaveAttribute("aria-sort", "descending");
});

test("an application's name and return addresses can be edited, and a refused edit keeps what was typed", async ({ page }) => {
  await signIn(page);
  const key = uniqueName("e2e");
  await (await createApplication(page, key)).getByRole("button", { name: "Done" }).click();

  await (await findRow(page, key)).getByRole("link", { name: key }).click();
  const form = page.locator("form", { has: page.locator('input[name="name"]') });
  await form.locator('input[name="name"]').fill("Edited name");
  await form.locator('textarea[name="postLogoutRedirectUris"]').fill("https://e2e.example/signed-out");
  await form.getByRole("button", { name: "Save" }).click();

  await expect(form.getByText("Saved.")).toBeVisible();
  await page.reload();
  await expect(form.locator('input[name="name"]')).toHaveValue("Edited name");
  await expect(form.locator('textarea[name="postLogoutRedirectUris"]')).toHaveValue("https://e2e.example/signed-out");

  // Plain http on another host is refused; the fields keep what was typed.
  await form.locator('textarea[name="redirectUris"]').fill("http://e2e.example/signin-oidc");
  await form.getByRole("button", { name: "Save" }).click();
  await expect(form.getByRole("alert").filter({ hasText: "https" })).toBeVisible();
  await expect(form.locator('textarea[name="redirectUris"]')).toHaveValue("http://e2e.example/signin-oidc");
});

test("deactivating asks first, and nothing changes when the admin says no", async ({ page }) => {
  await signIn(page);
  const key = uniqueName("e2e");
  await (await createApplication(page, key)).getByRole("button", { name: "Done" }).click();
  // Switching an application on and off is on its own page, not in the list.
  await (await findRow(page, key)).getByRole("link", { name: key }).click();
  const status = page.getByRole("heading", { level: 1 });

  // Saying no must send nothing at all. (Checking the status alone could pass before a change shows up.)
  const posts: string[] = [];
  page.on("request", request => {
    if (request.method() === "POST") {
      posts.push(request.url());
    }
  });
  await page.getByRole("button", { name: "Deactivate" }).click();
  await answer(page, "Cancel");
  // A form's request starts right after the click; give it time to show up if it was (wrongly) sent.
  await page.waitForTimeout(1000);
  expect(posts).toEqual([]);
  await expect(status.getByText("Active", { exact: true })).toBeVisible();

  await page.getByRole("button", { name: "Deactivate" }).click();
  await answer(page, "Confirm");
  await expect(status.getByText("Inactive", { exact: true })).toBeVisible();

  await page.getByRole("button", { name: "Activate" }).click();
  await expect(status.getByText("Active", { exact: true })).toBeVisible();
});

test("the admin UI's own application can't be deactivated", async ({ page }) => {
  await signIn(page);
  await page.goto("/applications");
  await (await findRow(page, "auth")).getByRole("link", { name: "auth" }).click();

  await expect(page.getByText("is always active")).toBeVisible();
  await expect(page.getByRole("button", { name: "Deactivate" })).toHaveCount(0);
});

test("a role's operations are added in a dialog and removed one by one", async ({ page }) => {
  await signIn(page);
  await page.goto("/applications");
  await (await findRow(page, clientApp)).getByRole("link", { name: "Roles" }).click();

  const roleName = uniqueName("E2e");
  await page.getByRole("button", { name: "New role" }).click();
  await page.getByRole("dialog").locator('input[name="name"]').fill(roleName);
  await page.getByRole("dialog").getByRole("button", { name: "Create" }).click();
  await page.getByRole("link", { name: roleName }).click();
  await expect(page.getByText("This role grants no operations yet.")).toBeVisible();

  // Pick the first operation in the multi-select; the button says how many are picked.
  await page.getByRole("button", { name: "Add operations" }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByRole("combobox", { name: "Operations" }).click();
  const first = page.getByRole("option").first();
  const operation = (await first.textContent())!.trim();
  await first.click();
  await page.keyboard.press("Escape");
  await dialog.getByRole("button", { name: "Add 1 operation" }).click();

  await expect(dialog).toHaveCount(0);
  const granted = rowOf(page, operation);
  await expect(granted).toBeVisible();

  await granted.getByRole("button", { name: "Remove" }).click();
  await answer(page, "Confirm");
  await expect(page.getByText("This role grants no operations yet.")).toBeVisible();

  // Clean up: deleting the role goes back to the application's roles.
  await page.getByRole("button", { name: "Delete role" }).click();
  await answer(page, "Confirm");
  await expect(page.getByRole("heading", { name: "Roles" }).first()).toBeVisible();
  await expect(page.getByRole("link", { name: roleName })).toHaveCount(0);
});

// A form inside a grid row keeps its state when the page refreshes after the action: before the fix, the grid
// rebuilt its cells after every action and the refusal vanished, so the admin saw nothing happen.
test("a refused change in a grid row says why", async ({ page }) => {
  await signIn(page);
  await page.goto("/applications");
  await (await findRow(page, "auth")).getByRole("link", { name: "Roles" }).click();
  await page.getByRole("link", { name: "Admin", exact: true }).click();

  // Nobody would hold auth.Admin any more, so Kale refuses.
  const row = rowOf(page, "auth.Admin");
  await row.getByRole("button", { name: "Remove" }).click();
  await answer(page, "Confirm");

  await expect(row.getByRole("alert").filter({ hasText: "AuthService refused the change" })).toBeVisible();
  await page.reload();
  await expect(rowOf(page, "auth.Admin")).toBeVisible();
});

test("a user's access row shows what was saved", async ({ page }) => {
  await signIn(page);
  await createUser(page, "Browser Test");

  await page.getByLabel("Application", { exact: true }).selectOption({ label: clientApp });
  await page.getByRole("button", { name: "Give access" }).click();
  const row = page.locator(`li[data-application="${clientApp}"]`);
  await expect(row).toBeVisible();

  // Inactive, with the first of the application's roles as the default. Before the fix (#50), the selects went
  // back to "active" and "no default role" right after saving, next to "Saved.".
  const access = row.getByLabel("Access");
  const defaultRole = row.getByLabel("Default role");
  await access.selectOption("false");
  await defaultRole.selectOption({ index: 1 });
  const roleId = await defaultRole.inputValue();
  await row.getByRole("button", { name: "Save" }).click();
  await answer(page, "Confirm");

  await expect(row.getByText("Saved.")).toBeVisible();
  await expect(access).toHaveValue("false");
  await expect(defaultRole).toHaveValue(roleId);

  await page.reload();
  await expect(page.locator(`li[data-application="${clientApp}"]`).getByLabel("Access")).toHaveValue("false");
  await expect(page.locator(`li[data-application="${clientApp}"]`).getByLabel("Default role")).toHaveValue(roleId);
});

test("signing in on another device ends this session, and signing in again asks for the password", async ({ page, browser }) => {
  await signIn(page);

  // The same admin on another device: Kale ends the first session (one per user and application).
  const otherDevice = await browser.newContext({ locale: "en-US" });
  await signIn(await otherDevice.newPage());
  await otherDevice.close();

  // The "session stopped" event reaches AuthService's admin API in about a second; until then it still answers.
  await expect(async () => {
    await page.goto("/applications");
    await expect(page).toHaveURL(/\/signed-out\?state=session-ended/, { timeout: 1000 });
  }).toPass({ timeout: 15_000 });
  await expect(page.getByRole("heading", { name: "Your session ended" })).toBeVisible();

  // Not straight back in through AuthService's own cookie (that would end the other device's session in turn).
  // A reload (or Back, or another tab with the same cookie) stays on "ended" instead of signing in silently.
  await page.goto("/");
  await expect(page).toHaveURL(/\/signed-out\?state=session-ended/);
  await page.getByRole("link", { name: "Sign in again" }).click();
  await expect(page.locator('input[name="Password"]')).toBeVisible();
});

test("a test token lets a client app (SampleApp) be called as a user", async ({ page }) => {
  await signIn(page);

  // A user with access to "sample" and one of its roles (the smoke test's roles hold sample.NoteRead).
  const userName = await createUser(page, "Token Test");
  await page.getByLabel("Application", { exact: true }).selectOption({ label: "sample" });
  await page.getByRole("button", { name: "Give access" }).click();
  await page.getByRole("button", { name: "Assign roles" }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByRole("combobox", { name: "Roles (direct)" }).click();
  await page.getByPlaceholder("Search…").fill("Reader-");
  await page.getByRole("option", { name: /^Reader-/ }).first().click();
  await page.keyboard.press("Escape");
  await dialog.getByRole("button", { name: "Assign 1 role" }).click();
  await expect(page.getByRole("button", { name: "Remove" })).toBeVisible();

  await page.goto("/test-token");
  await page.getByLabel("Application").selectOption("sample");
  await page.getByLabel("User").selectOption({ label: `${userName} (Token Test)` });
  await page.getByRole("button", { name: "Get token" }).click();
  const token = (await page.locator("[data-secret]").textContent())!;

  const notes = await page.request.get(`${process.env.KALE_E2E_SAMPLE_URL}/notes`, { headers: { Authorization: `Bearer ${token}` } });
  expect(notes.status()).toBe(200);
  const anonymous = await page.request.get(`${process.env.KALE_E2E_SAMPLE_URL}/notes`);
  expect(anonymous.status()).toBe(401);
});

test("signing out ends the session; the next visit signs in again", async ({ page }) => {
  await signIn(page);

  await page.getByRole("button", { name: "Sign out" }).click();

  await expect(page).toHaveURL(/\/signed-out/);
  await page.goto("/");
  await expect(page.locator('input[name="UserName"]')).toBeVisible();
});
