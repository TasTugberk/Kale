import { randomBytes } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";

/**
 * The admin UI in a real browser, with JavaScript: what the plain form posts of smoke-test.sh can't show (forms
 * after an action, confirmation dialogs). Creates an application and a user per run, like the smoke test.
 */

const admin = {
  userName: process.env.KALE_E2E_ADMIN_USER ?? "admin",
  password: process.env.KALE_E2E_ADMIN_PASSWORD ?? "",
};

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
  const form = page.locator("form.stacked");
  await form.locator('input[name="key"]').fill(key);
  await form.locator('input[name="name"]').fill("Browser test");
  await form.locator('textarea[name="redirectUris"]').fill("https://e2e.example/signin-oidc");
  await form.getByRole("button", { name: "Create" }).click();
  await expect(rowOf(page, key)).toBeVisible();
}

// The table row whose <code> is exactly this key.
function rowOf(page: Page, key: string) {
  return page.locator("tr", { has: page.locator("code", { hasText: new RegExp(`^${key}$`) }) });
}

test.beforeAll(() => {
  if (!admin.password) {
    throw new Error("Set KALE_E2E_ADMIN_PASSWORD (scripts/browser-test.sh does).");
  }
});

test("a new application's secret shows once, and a refused form keeps what was typed", async ({ page }) => {
  await signIn(page);
  const key = uniqueName("e2e");
  await createApplication(page, key);
  const form = page.locator("form.stacked");

  await expect(page.getByRole("status").filter({ hasText: `Client secret of ${key}` })).toBeVisible();
  await expect(page.locator(".secret code")).not.toBeEmpty();
  await expect(rowOf(page, key)).toBeVisible();

  // The same key again: refused, and the fields still hold what was typed (React empties a form after its action).
  await form.locator('input[name="key"]').fill(key);
  await form.locator('input[name="name"]').fill("Typed again");
  await form.locator('textarea[name="redirectUris"]').fill("https://e2e.example/signin-oidc");
  await form.getByRole("button", { name: "Create" }).click();

  await expect(page.getByRole("alert").filter({ hasText: "already exists" })).toBeVisible();
  await expect(form.locator('input[name="key"]')).toHaveValue(key);
  await expect(form.locator('input[name="name"]')).toHaveValue("Typed again");

  // Shown once: after a reload it's gone.
  await page.reload();
  await expect(page.locator(".secret")).toHaveCount(0);
});

test("deactivating asks first, and nothing changes when the admin says no", async ({ page }) => {
  await signIn(page);
  const key = uniqueName("e2e");
  await createApplication(page, key);
  const row = rowOf(page, key);

  // Saying no must send nothing at all. (Checking the status alone could pass before a change shows up.)
  const posts: string[] = [];
  page.on("request", request => {
    if (request.method() === "POST") {
      posts.push(request.url());
    }
  });
  page.once("dialog", dialog => dialog.dismiss());
  await row.getByRole("button", { name: "Deactivate" }).click();
  // A form's request starts right after the click; give it time to show up if it was (wrongly) sent.
  await page.waitForTimeout(1000);
  expect(posts).toEqual([]);
  await expect(row.getByRole("cell", { name: "Active", exact: true })).toBeVisible();

  page.once("dialog", dialog => dialog.accept());
  await row.getByRole("button", { name: "Deactivate" }).click();
  await expect(row.getByRole("cell", { name: "Inactive", exact: true })).toBeVisible();

  await row.getByRole("button", { name: "Activate" }).click();
  await expect(row.getByRole("cell", { name: "Active", exact: true })).toBeVisible();
});

test("a user's access row shows what was saved", async ({ page }) => {
  await signIn(page);
  await page.goto("/users");
  const userName = uniqueName("e2e");
  const form = page.locator("form.stacked");
  await form.locator('input[name="userName"]').fill(userName);
  await form.locator('input[name="displayName"]').fill("Browser Test");
  await form.locator('input[name="password"]').fill(`E2e-${randomBytes(8).toString("hex")}`);
  await form.getByRole("button", { name: "Create" }).click();
  await page.getByRole("status").getByRole("link", { name: userName }).click();

  await page.getByLabel("Application").selectOption({ label: "sample" });
  await page.getByRole("button", { name: "Give access" }).click();
  const row = rowOf(page, "sample");
  await expect(row).toBeVisible();

  // Inactive, with the first of sample's roles as the default. Before the fix (#50), the selects went back to
  // "active" and "no default role" right after saving, next to "Saved.".
  const access = row.getByLabel("Access");
  const defaultRole = row.getByLabel("Default role");
  await access.selectOption("false");
  await defaultRole.selectOption({ index: 1 });
  const roleId = await defaultRole.inputValue();
  page.once("dialog", dialog => dialog.accept());
  await row.getByRole("button", { name: "Save" }).click();

  await expect(row.getByText("Saved.")).toBeVisible();
  await expect(access).toHaveValue("false");
  await expect(defaultRole).toHaveValue(roleId);

  await page.reload();
  await expect(rowOf(page, "sample").getByLabel("Access")).toHaveValue("false");
  await expect(rowOf(page, "sample").getByLabel("Default role")).toHaveValue(roleId);
});

test("signing out ends the session; the next visit signs in again", async ({ page }) => {
  await signIn(page);

  await page.getByRole("button", { name: "Sign out" }).click();

  await expect(page).toHaveURL(/\/signed-out/);
  await page.goto("/");
  await expect(page.locator('input[name="UserName"]')).toBeVisible();
});
