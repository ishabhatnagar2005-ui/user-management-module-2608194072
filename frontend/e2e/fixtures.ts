/** Capture browser failures before navigation and use only real, operator-configured sessions. */
import { test as base, expect, Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
export interface DevelopmentConfig { Seed: { UserUsername: string; UserPassword: string; AdminUsername: string; AdminPassword: string }; }
/** Read synthetic seed configuration without logging credentials or copying them into source. */
export function credentials(role: 'User' | 'Admin' = 'User'): { username: string; password: string } {
  const prefix = role === 'Admin' ? 'E2E_ADMIN' : 'E2E_USER';
  const username = process.env[`${prefix}_USERNAME`]; const password = process.env[`${prefix}_PASSWORD`];
  if (username !== undefined || password !== undefined) {
    if (!username || !password) throw new Error(`Supply both ${prefix}_USERNAME and ${prefix}_PASSWORD with non-empty values.`);
    return { username, password };
  }
  const config = JSON.parse(fs.readFileSync(path.resolve(__dirname, '../../backend/appsettings.Development.json'), 'utf8')) as DevelopmentConfig;
  return { username: role === 'Admin' ? config.Seed.AdminUsername : config.Seed.UserUsername, password: role === 'Admin' ? config.Seed.AdminPassword : config.Seed.UserPassword };
}
/** Fill the existing login page and assert the live API's token response. */
export async function signIn(page: Page, role: 'User' | 'Admin' = 'User'): Promise<void> {
  const account = credentials(role);
  await page.getByLabel('Username', { exact: true }).fill(account.username);
  await page.getByLabel('Password', { exact: true }).fill(account.password);
  const response = page.waitForResponse(r => new URL(r.url()).pathname === '/api/login' && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  const login = await response; expect(login.status()).toBe(200);
  const body = await login.json() as { accessToken: string; tokenType: string; expiresAt: string };
  expect(body.tokenType).toBe('Bearer'); expect(body.accessToken.split('.')).toHaveLength(3); expect(Date.parse(body.expiresAt)).toBeGreaterThan(Date.now());
  await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();
}
export const test = base.extend<{ consoleErrors: string[] }>({
  consoleErrors: [async ({ page }, use, testInfo) => {
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
    await use(errors);
    // Chromium reports an intentional HTTP denial as a resource error. Permit only the test's exact declared denial.
    const allowedStatus = testInfo.annotations.find(a => a.type === 'expected-http-console-status')?.description;
    const unexpected = errors.filter(e => !(allowedStatus && /^Failed to load resource: the server responded with a status of /.test(e) && e.includes(`status of ${allowedStatus} `)));
    expect(unexpected, 'Unexpected browser console errors').toEqual([]);
  }, { auto: true }]
});
export { expect };
